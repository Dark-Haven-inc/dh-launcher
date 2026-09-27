using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher;
using DarkHaven.Launcher.Local;
using DarkHaven.Launcher.Servers;

namespace DarkHaven.App.ViewModels;

/// <summary>A choice in a server's "Сборка" list: a specific CDN build, or "always the newest".</summary>
public sealed record LocalBuildChoice(string? Version, string Title);

/// <summary>A CDN build in the СБОРКИ list, and whether it's on disk.</summary>
public sealed partial class LocalBuildRow(LocalBuild build, bool installed) : ObservableObject
{
    public LocalBuild Build => build;
    public string Title => $"{build.ShortVersion} · {RuText.ShortDateTime(build.Time.ToLocalTime())}";
    [ObservableProperty] private bool _installed = installed;
    [ObservableProperty] private string? _progress;

    public string Subtitle =>
        (build.Size is { } b ? $"{b / (1024 * 1024)} МБ" : "размер неизвестен") + (Installed ? " · скачана" : "");

    public string DownloadLabel => Progress ?? "Скачать";

    partial void OnInstalledChanged(bool value) => OnPropertyChanged(nameof(Subtitle));
    partial void OnProgressChanged(string? value) => OnPropertyChanged(nameof(DownloadLabel));
}

/// <summary>One local server: its settings (editable while it's stopped), state, and console.</summary>
public sealed partial class LocalServerRow : ObservableObject
{
    private readonly AppServices _services;
    private readonly LocalServers _local;
    private readonly DispatcherTimer _consoleFlush = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _consoleDirty;
    private bool _loading;

    public LocalServerProfile Profile { get; }
    public LocalServerHost Host { get; private set; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private LocalServerMode _mode;
    [ObservableProperty] private LocalBuildChoice? _buildChoice;
    [ObservableProperty] private string _port;
    [ObservableProperty] private string _maxPlayers;
    [ObservableProperty] private string _map;
    [ObservableProperty] private string _command = "";
    [ObservableProperty] private string _consoleText = "";
    [ObservableProperty] private bool _confirmDelete;
    [ObservableProperty] private string? _settingsError;

    public LocalServerRow(AppServices services, LocalServerProfile profile)
    {
        _services = services;
        _local = services.Local;
        Profile = profile;
        _loading = true;
        _name = profile.Name;
        _mode = profile.Mode;
        _shared = profile.Shared;
        _port = profile.Port.ToString();
        _maxPlayers = profile.MaxPlayers.ToString();
        _map = profile.Map ?? "";
        _loading = false;

        Host = _local.HostFor(profile);
        Attach();
        _consoleFlush.Tick += (_, _) =>
        {
            if (!_consoleDirty) return;
            _consoleDirty = false;
            ConsoleText = string.Join('\n', Host.Lines.TakeLast(400));
        };
        _consoleFlush.Start();
        ConsoleText = string.Join('\n', Host.Lines.TakeLast(400));
    }

    private void Attach()
    {
        Host.Changed += () => Dispatcher.UIThread.Post(RaiseState);
        Host.LineAdded += _ => _consoleDirty = true;
    }

    public LocalServerState State => Host.State;
    public bool IsStopped => !Host.IsBusy;
    public bool IsRunning => Host.State == LocalServerState.Running;
    public bool CanStop => Host.State is LocalServerState.Starting or LocalServerState.Running;
    public bool HasConsole => Host.State is LocalServerState.Starting or LocalServerState.Running;

    public string ModeText => Mode == LocalServerMode.Develop ? "маппинг и тесты" : "игра";

    public string StateText => Host.State switch
    {
        LocalServerState.Preparing => Host.Detail ?? "подготовка…",
        LocalServerState.Starting => Host.Detail ?? "запускается…",
        LocalServerState.Running => $"работает · {Host.Address}",
        LocalServerState.Stopping => "останавливается…",
        LocalServerState.Crashed => Host.Detail ?? "остановился с ошибкой",
        _ => Profile.LastStartedAt is { } at ? $"выключен · запускался {RuText.DayTime(at.ToLocalTime())}" : "выключен · ещё не запускался",
    };

    /// <summary>The StatusGlyph kind: ■ running, ◧ on its way up or down, ✕ crashed, □ off.</summary>
    public string Dot => Host.State switch
    {
        LocalServerState.Running => "on",
        LocalServerState.Preparing or LocalServerState.Starting or LocalServerState.Stopping => "full",
        LocalServerState.Crashed => "off",
        _ => "q",
    };

    private void RaiseState()
    {
        foreach (var name in new[] { nameof(State), nameof(IsStopped), nameof(IsRunning), nameof(CanStop), nameof(HasConsole), nameof(StateText), nameof(Dot) })
            OnPropertyChanged(name);
        _consoleDirty = true;
        AccessFollowState();
    }

    // Settings save as they're typed; numbers only once they're valid.
    partial void OnNameChanged(string value) => SaveIf(() => Profile.Name = string.IsNullOrWhiteSpace(value) ? "Мой сервер" : value.Trim()[..Math.Min(value.Trim().Length, 60)]);
    partial void OnModeChanged(LocalServerMode value) { SaveIf(() => Profile.Mode = value); OnPropertyChanged(nameof(ModeText)); }
    partial void OnMapChanged(string value) => SaveIf(() => Profile.Map = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    partial void OnBuildChoiceChanged(LocalBuildChoice? value) => SaveIf(() => Profile.Build = value?.Version);

    partial void OnPortChanged(string value)
    {
        if (int.TryParse(value, out var port) && port is >= 1024 and <= 65535)
        {
            SettingsError = null;
            SaveIf(() => Profile.Port = port);
            if (!Host.IsBusy)
            {
                Host = _local.HostFor(Profile);
                Attach();
                RaiseState();
            }
        }
        else
            SettingsError = "Порт — число от 1024 до 65535.";
    }

    partial void OnMaxPlayersChanged(string value)
    {
        if (int.TryParse(value, out var n) && n is >= 1 and <= 200)
        {
            SettingsError = null;
            SaveIf(() => Profile.MaxPlayers = n);
        }
        else
            SettingsError = "Игроков — от 1 до 200.";
    }

    private void SaveIf(Action apply)
    {
        if (_loading)
            return;
        apply();
        _local.Profiles.Save(Profile);
    }

    [RelayCommand] private void SetMode(string mode) => Mode = mode == "play" ? LocalServerMode.Play : LocalServerMode.Develop;

    [RelayCommand]
    private void SendCommand()
    {
        var text = Command.Trim();
        if (text.Length == 0) return;
        Host.Send(text);
        Command = "";
    }
}

/// <summary>A friend's open server, or one I'm invited to.</summary>
public sealed class LocalAvailableRow(DarkHaven.Launcher.Api.PlatformLocalAvailable a)
{
    public int Id => a.Id;
    public string OwnerName => a.OwnerName;
    public string Name => a.Name;
    public string Title => $"{a.OwnerName} · {a.Name}";
    public string Subtitle => $"{(a.Mode == "develop" ? "маппинг и тесты" : "игра")} · игроков: {a.Players}";
    public string? Address => a.Address;
    public bool CanJoin => a.Status == "invited" && a.Address is not null;
    public bool CanAsk => a.Status == "none";
    public bool Asked => a.Status == "requested";
}

/// <summary>
/// ЛОКАЛКА — DH servers on the player's own PC, for playing on their own, testing, and building maps.
/// Pick a mode and a build, press start; the build and .NET are fetched on the first run. Connecting
/// goes through the normal connect card, to <c>ss14://127.0.0.1:port</c>.
/// </summary>
public partial class LocalViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly Action<ServerEntry> _connect;

    public ObservableCollection<LocalServerRow> Servers { get; } = [];
    public ObservableCollection<LocalBuildRow> Builds { get; } = [];
    public ObservableCollection<LocalBuildChoice> BuildChoices { get; } = [];
    /// <summary>Friends' open servers and ones I'm invited to.</summary>
    public ObservableCollection<LocalAvailableRow> Available { get; } = [];
    public bool HasAvailable => LocalServers.SharingEnabled && Available.Count > 0;
    [ObservableProperty] private string? _availableMessage;

    [ObservableProperty] private LocalServerRow? _selected;
    [ObservableProperty] private string? _buildsMessage;
    [ObservableProperty] private bool _loadingBuilds;

    public bool HasServers => Servers.Count > 0;
    public bool HasSelected => Selected is not null;

    public LocalViewModel(AppServices services, Action<ServerEntry> connect)
    {
        _services = services;
        _connect = connect;
        BuildChoices.Add(new LocalBuildChoice(null, "Новейшая"));
        foreach (var p in services.Local.Profiles.List())
            Servers.Add(new LocalServerRow(services, p));
        Selected = Servers.FirstOrDefault();
        SyncBuildChoice();
    }

    partial void OnSelectedChanged(LocalServerRow? value)
    {
        OnPropertyChanged(nameof(HasSelected));
        SyncBuildChoice();
    }

    /// <summary>The page opened: refresh the build list (cheap, one small JSON).</summary>
    public void Activate()
    {
        _ = LoadBuildsAsync();
        if (LocalServers.SharingEnabled)
            _ = LoadAvailableAsync();
    }

    [RelayCommand]
    private async Task LoadAvailableAsync()
    {
        var list = _services.Platform.IsSignedIn
            ? await _services.Platform.GetAvailableLocalAsync()
            : [];
        Available.Clear();
        foreach (var a in list)
            Available.Add(new LocalAvailableRow(a));
        OnPropertyChanged(nameof(HasAvailable));
    }

    [RelayCommand]
    private async Task AskToJoin(LocalAvailableRow row)
    {
        var error = await _services.Platform.AskToJoinLocalAsync(row.Id);
        AvailableMessage = error ?? $"Запрос отправлен — {row.OwnerName} увидит его у себя в ЛОКАЛКЕ.";
        await LoadAvailableAsync();
    }

    [RelayCommand]
    private void JoinShared(LocalAvailableRow row)
    {
        if (row is { CanJoin: true, Address: { } address })
            _connect(new ServerEntry(address) { Name = $"{row.OwnerName}: {row.Name}" });
    }

    [RelayCommand]
    private async Task LoadBuildsAsync()
    {
        LoadingBuilds = true;
        try
        {
            var list = await _services.Local.Catalog.GetAsync();
            Builds.Clear();
            foreach (var b in list.Builds)
                Builds.Add(new LocalBuildRow(b, _services.Local.Builds.IsInstalled(b.Version)));

            var keep = Selected?.BuildChoice?.Version;
            BuildChoices.Clear();
            BuildChoices.Add(new LocalBuildChoice(null, "Новейшая"));
            foreach (var b in list.Builds)
                BuildChoices.Add(new LocalBuildChoice(b.Version, $"{b.ShortVersion} · {RuText.ShortDate(b.Time.ToLocalTime())}"));
            SyncBuildChoice(keep);

            BuildsMessage = list switch
            {
                { Builds.Count: 0, WithoutServer: > 0 } =>
                    "Сборок для локалки под эту систему пока нет. " +
                    "Как только появится, сервер можно будет запустить.",
                { Builds.Count: 0 } => "Не удалось получить список сборок. Проверьте интернет и нажмите «Обновить».",
                { StaleSince: { } since } => $"Список сборок не обновился — показан от {RuText.DayTime(since.ToLocalTime())}.",
                _ => null,
            };
        }
        finally
        {
            LoadingBuilds = false;
        }
    }

    private void SyncBuildChoice(string? version = null)
    {
        if (Selected is not { } row)
            return;
        version ??= row.Profile.Build;
        var choice = BuildChoices.FirstOrDefault(c => c.Version == version);
        if (choice is null && version is not null)
        {
            // Pinned to a build the CDN no longer lists (it may still be on disk) — keep showing it.
            choice = new LocalBuildChoice(version, $"{(version.Length > 8 ? version[..8] : version)} (нет в списке)");
            BuildChoices.Add(choice);
        }
        row.BuildChoice = choice ?? BuildChoices[0];
    }

    [RelayCommand]
    private void NewServer(string mode)
    {
        var develop = mode != "play";
        var p = _services.Local.Profiles.New(
            develop ? $"Маппинг {Servers.Count + 1}" : $"Сервер {Servers.Count + 1}",
            develop ? LocalServerMode.Develop : LocalServerMode.Play);
        _services.Local.Profiles.Save(p);
        var row = new LocalServerRow(_services, p);
        Servers.Add(row);
        Selected = row;
        OnPropertyChanged(nameof(HasServers));
    }

    [RelayCommand]
    private async Task Start(LocalServerRow? row)
    {
        row ??= Selected;
        if (row is null || !row.IsStopped) return;
        if (LocalServers.SharingEnabled && row.Profile.Shared && !_services.Platform.IsSignedIn)
        {
            row.Host.Failed("Чтобы пускать других, войдите в аккаунт: без него белый список сервера не пустит даже вас.");
            return;
        }
        await _services.Local.StartAsync(row.Profile);
        await LoadBuildsAsync(); // a first start downloads a build
    }

    [RelayCommand]
    private Task Stop(LocalServerRow? row) => (row ?? Selected)?.Host.StopAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private void Connect(LocalServerRow? row)
    {
        row ??= Selected;
        if (row is not { IsRunning: true }) return;
        _connect(new ServerEntry(row.Host.Address) { Name = row.Name });
    }

    [RelayCommand]
    private void OpenFolder(LocalServerRow? row)
    {
        row ??= Selected;
        if (row is null) return;
        var dir = _services.Local.Profiles.DataDirFor(row.Profile.Id);
        Directory.CreateDirectory(dir);
        try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch { /* no file manager — nothing to do */ }
    }

    [RelayCommand]
    private async Task Delete(LocalServerRow? row)
    {
        row ??= Selected;
        if (row is null) return;
        if (!row.ConfirmDelete)
        {
            row.ConfirmDelete = true;
            return;
        }
        if (row.Host.IsBusy)
            await row.Host.StopAsync();
        _services.Local.Profiles.Delete(row.Profile.Id);
        Servers.Remove(row);
        Selected = Servers.FirstOrDefault();
        OnPropertyChanged(nameof(HasServers));
    }

    [RelayCommand]
    private void CancelDelete(LocalServerRow? row) => (row ?? Selected)!.ConfirmDelete = false;

    [RelayCommand]
    private async Task DownloadBuild(LocalBuildRow row)
    {
        if (row.Installed || row.Progress is not null) return;
        try
        {
            row.Progress = "0%";
            await _services.Local.Builds.InstallAsync(row.Build, new Progress<double>(f => row.Progress = $"{f:P0}"));
            row.Installed = true;
        }
        catch (Exception e)
        {
            BuildsMessage = e is HttpRequestException ? "Сборка не скачалась — нет связи с CDN." : e.Message;
        }
        finally
        {
            row.Progress = null;
        }
    }

    [RelayCommand]
    private void DeleteBuild(LocalBuildRow row)
    {
        var inUse = Servers.Any(s => s.Host.IsBusy && (s.Profile.Build == row.Build.Version || s.Profile.Build is null));
        if (inUse)
        {
            BuildsMessage = "Сначала остановите серверы, которые запущены на этой сборке.";
            return;
        }
        try
        {
            _services.Local.Builds.Delete(row.Build.Version);
            row.Installed = false;
        }
        catch (Exception e)
        {
            BuildsMessage = $"Не удалось удалить: {e.Message}";
        }
    }
}
