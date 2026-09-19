using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Models;
using DarkHaven.Launcher.Servers;

namespace DarkHaven.App.ViewModels;

public sealed partial class MonitorRegion(string name, string address) : ObservableObject
{
    public string Name { get; } = name;
    public string Address { get; } = address;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>One row of the table view — the chart's values without hovering.</summary>
public sealed record HistoryRow(string When, string Average, string Peak, string Uptime);

/// <summary>
/// МОНИТОРИНГ: a region's state right now (read straight from its /status, so the ping is the
/// player's own) and its online history from the platform, for a day, a week or a month.
/// Polls only while the tab is open.
/// </summary>
public partial class MonitoringViewModel(AppServices services, Action<ServerEntry> connect) : ViewModelBase
{
    private static readonly TimeSpan LiveEvery = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HistoryEvery = TimeSpan.FromMinutes(1);

    private DispatcherTimer? _timer;
    private DateTime _historyAt = DateTime.MinValue;
    private HashSet<string>? _tracked;

    public ObservableCollection<MonitorRegion> Regions { get; } = [];
    public ObservableCollection<HistoryRow> Rows { get; } = [];

    [ObservableProperty] private MonitorRegion? _selected;

    // Right now
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private string _stateText = "…";
    [ObservableProperty] private string _playersText = "—";
    [ObservableProperty] private string _mapText = "—";
    [ObservableProperty] private string _presetText = "—";
    [ObservableProperty] private string _roundText = "—";
    [ObservableProperty] private string _pingText = "—";
    [ObservableProperty] private int _capacity;

    // History
    [ObservableProperty] private string _range = "24h";
    [ObservableProperty] private IReadOnlyList<OnlinePoint>? _points;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _emptyText = "Загружаю…";
    [ObservableProperty] private string _peakText = "—";
    [ObservableProperty] private string _averageText = "—";
    [ObservableProperty] private string _uptimeText = "—";
    [ObservableProperty] private bool _showTable;
    [ObservableProperty] private string _copyLabel = "Скопировать адрес";

    public bool HasRegions => Regions.Count > 0;
    public string TableLabel => ShowTable ? "Скрыть таблицу" : "Показать таблицей";
    partial void OnShowTableChanged(bool value) => OnPropertyChanged(nameof(TableLabel));

    /// <summary>Tab opened: pick up the region list, start polling.</summary>
    public void Activate()
    {
        var regions = services.Regions.Regions
            .Where(r => !r.IsQuarantine && !string.IsNullOrWhiteSpace(r.Address))
            .ToList();
        if (Regions.Select(r => r.Name).SequenceEqual(regions.Select(r => r.Name)) is false)
        {
            Regions.Clear();
            foreach (var r in regions)
                Regions.Add(new MonitorRegion(r.Name, r.Address));
            OnPropertyChanged(nameof(HasRegions));
        }
        if (Selected is null || !Regions.Contains(Selected))
            SelectRegion(Regions.FirstOrDefault());

        _timer ??= new DispatcherTimer(LiveEvery, DispatcherPriority.Background, (_, _) => _ = TickAsync());
        _timer.Start();
        _historyAt = DateTime.MinValue;
        _ = TickAsync();
    }

    /// <summary>Tab closed: no polling in the background.</summary>
    public void Deactivate() => _timer?.Stop();

    private async Task TickAsync()
    {
        await RefreshLiveAsync();
        if (DateTime.UtcNow - _historyAt >= HistoryEvery)
            await RefreshHistoryAsync();
    }

    [RelayCommand]
    private void SelectRegion(MonitorRegion? region)
    {
        if (region is null) return;
        foreach (var r in Regions) r.IsSelected = ReferenceEquals(r, region);
        Selected = region;
        StateText = "…";
        Points = null;
        _historyAt = DateTime.MinValue;
        _ = TickAsync();
    }

    [RelayCommand]
    private void SetRange(string range)
    {
        if (Range == range) return;
        Range = range;
        _ = RefreshHistoryAsync();
    }

    [RelayCommand]
    private async Task Refresh()
    {
        _historyAt = DateTime.MinValue;
        await TickAsync();
    }

    [RelayCommand]
    private void Play()
    {
        if (Selected is { } r)
            connect(new ServerEntry(r.Address) { Name = r.Name });
    }

    [RelayCommand]
    private async Task CopyAddress()
    {
        if (Selected is null) return;
        await App.CopyToClipboardAsync(Selected.Address);
        CopyLabel = "Скопировано";
        await Task.Delay(2000);
        CopyLabel = "Скопировать адрес";
    }

    [RelayCommand]
    private void ToggleTable() => ShowTable = !ShowTable;

    private async Task RefreshLiveAsync()
    {
        if (Selected is not { } region || !Ss14Address.TryParse(region.Address, out var uri))
            return;

        ServerStatus? status = null;
        long ms = 0;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var sw = Stopwatch.StartNew();
            status = await services.Http.GetFromJsonAsync<ServerStatus>(Ss14Address.StatusAddress(uri), LauncherJson.Options, timeout.Token);
            ms = sw.ElapsedMilliseconds;
        }
        catch { /* offline — shown below */ }

        if (!ReferenceEquals(region, Selected))
            return; // switched regions while this was in flight

        IsOnline = status is not null;
        if (status is null)
        {
            StateText = "Сервер не отвечает";
            PlayersText = MapText = PresetText = RoundText = PingText = "—";
            return;
        }

        Capacity = Math.Max(0, status.SoftMaxPlayers);
        PlayersText = status.SoftMaxPlayers > 0 ? $"{status.Players} / {status.SoftMaxPlayers}" : status.Players.ToString();
        MapText = string.IsNullOrWhiteSpace(status.Map) ? "—" : status.Map!;
        PresetText = string.IsNullOrWhiteSpace(status.Preset) ? "—" : status.Preset!;
        RoundText = status.RoundId is { } id ? $"#{id}" : "—";
        PingText = $"{ms} мс";
        StateText = status.RunLevel switch
        {
            RunLevel.PreRoundLobby => "Лобби — сервер принимает игроков",
            RunLevel.InRound => "Раунд идёт — можно заходить",
            RunLevel.PostRound => "Конец раунда",
            _ => "Сервер онлайн",
        };
    }

    private async Task RefreshHistoryAsync()
    {
        if (Selected is not { } region)
            return;
        _historyAt = DateTime.UtcNow;

        if (!services.Platform.IsConfigured)
        {
            EmptyText = "История онлайна появится, когда лаунчер подключится к платформе Frontier 15.";
            Points = null;
            return;
        }

        IsLoading = true; // the chart keeps its last frame, dimmed — no flash, no layout jump
        try
        {
            _tracked ??= (await services.Platform.GetMonitoredServersAsync())
                .Select(s => s.Region).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (_tracked.Count > 0 && !_tracked.Contains(region.Name))
            {
                EmptyText = "Этот регион платформа пока не отслеживает.";
                Points = null;
                ClearSummary();
                return;
            }

            var range = Range;
            var history = await services.Platform.GetOnlineHistoryAsync(region.Name, range);
            if (!ReferenceEquals(region, Selected) || range != Range)
                return; // superseded by a newer choice
            if (history is null)
            {
                _tracked = null; // ask again next time — the platform may have been down
                EmptyText = "Платформа сейчас недоступна — история не загрузилась.";
                return;
            }

            EmptyText = "Данных пока нет — платформа записывает онлайн раз в минуту.";
            Points = history.Points;

            var s = history.Summary;
            PeakText = s.Peak is { } peak && s.PeakAt is { } at ? $"{peak} · {RuText.DayTime(at.ToLocalTime())}" : "—";
            AverageText = s.Average is { } avg ? avg.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "—";
            UptimeText = s.UptimePercent is { } up ? $"{up:0.#}%" : "—";

            Rows.Clear();
            foreach (var p in Enumerable.Reverse(history.Points).Where(p => p.Uptime is not null))
            {
                Rows.Add(new HistoryRow(
                    RuText.DayTime(p.At.ToLocalTime()),
                    p.Average is { } a ? a.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "—",
                    p.Peak?.ToString() ?? "—",
                    $"{p.Uptime!.Value * 100:0}%"));
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ClearSummary()
    {
        PeakText = AverageText = UptimeText = "—";
        Rows.Clear();
    }
}
