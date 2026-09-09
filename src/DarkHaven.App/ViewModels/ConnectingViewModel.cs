using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Servers;
using DarkHaven.Launcher.Update;
using Serilog;

namespace DarkHaven.App.ViewModels;

public partial class ChecklistStepViewModel(LaunchStep step, string title) : ViewModelBase
{
    public LaunchStep Step { get; } = step;
    public string Title { get; } = title;

    [ObservableProperty] private StepState _state = StepState.Pending;
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private bool _showBar;

    public bool IsActive => State == StepState.Active;
    public bool IsDone => State == StepState.Done;
    public bool IsPending => State == StepState.Pending;

    partial void OnStateChanged(StepState value)
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsPending));
    }
}

public partial class ConnectingViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly ServerEntry _server;
    private readonly CancellationTokenSource _cts = new();

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _speedEta = "";
    [ObservableProperty] private string _buildTag = "";
    [ObservableProperty] private bool _isBusy = true;
    [ObservableProperty] private string? _errorText;

    public ObservableCollection<ChecklistStepViewModel> Steps { get; } =
    [
        new(LaunchStep.Engine, "Версия движка"),
        new(LaunchStep.Content, "Загрузка контента"),
        new(LaunchStep.Verify, "Проверка файлов"),
        new(LaunchStep.Start, "Запуск игры"),
    ];

    public event Action? Finished;

    public ConnectingViewModel(AppServices services, ServerEntry server)
    {
        _services = services;
        _server = server;
        _title = server.DisplayName;
        _subtitle = server.IsDarkHavenRegion ? "Переход в регион" : "Подключение к серверу";
    }

    public async void Start()
    {
        var progress = new Progress<LaunchProgress>(p => Dispatcher.UIThread.Post(() => Apply(p)));
        _services.Discord.SetConnecting(_server.DisplayName);

        try
        {
            var compat = _services.Settings.GetConfig("CompatMode") == "true";
            var proc = await _services.Launch.ConnectAsync(_server.Address, allowGuest: true, compat, progress, _cts.Token);

            try { _services.Settings.RecordRecent(_server.Address, _server.DisplayName, _server.IsDarkHavenRegion); }
            catch (Exception e) { Log.Warning(e, "Could not record recent server"); }

            _services.Discord.SetInGame(_server.DisplayName, _server.IsDarkHavenRegion);
            _ = WatchProcessAsync(proc);
        }
        catch (OperationCanceledException)
        {
            _services.Discord.SetIdle();
            Close();
        }
        catch (Exception e)
        {
            Log.Error(e, "Connect failed");
            _services.Discord.SetIdle();
            IsBusy = false;
            var active = Steps.FirstOrDefault(s => s.State == StepState.Active);
            if (active is not null) active.State = StepState.Failed;
            ErrorText = e.Message;
        }
    }

    private void Apply(LaunchProgress p)
    {
        var row = Steps.First(s => s.Step == p.Step);
        row.State = p.State;
        if (p.Detail.Length > 0) row.Detail = p.Detail;
        if (p.Fraction is { } f)
        {
            row.Fraction = f * 100;
            row.ShowBar = true;
        }
        else if (p.State == StepState.Done)
        {
            row.ShowBar = false;
        }

        if (p.Step == LaunchStep.Content && p.State == StepState.Done)
            BuildTag = row.Detail;

        SpeedEta = FormatSpeedEta(p.BytesPerSecond, p.Eta);
    }

    private static string FormatSpeedEta(double? bps, TimeSpan? eta)
    {
        if (bps is not { } b || b < 1) return "";
        var speed = b switch
        {
            >= 1_000_000 => $"{b / 1_000_000:0.0} МБ/с",
            >= 1_000 => $"{b / 1_000:0} КБ/с",
            _ => $"{b:0} Б/с",
        };
        return eta is { } e && e.TotalSeconds is > 0 and < 86400
            ? $"{speed} · осталось ~{(int)e.TotalSeconds} с"
            : speed;
    }

    private async Task WatchProcessAsync(Process proc)
    {
        var exited = proc.WaitForExitAsync();
        await Task.WhenAny(exited, Task.Delay(1500));

        Dispatcher.UIThread.Post(() =>
        {
            if (proc.HasExited && proc.ExitCode != 0)
            {
                _services.Discord.SetIdle();
                IsBusy = false;
                ErrorText = $"Клиент завершился с ошибкой (код {proc.ExitCode}). Смотрите лог лаунчера.";
            }
            else
            {
                Close();
            }
        });

        _ = exited.ContinueWith(_ => _services.Discord.SetIdle(), TaskScheduler.Default);
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts.Cancel();
        Close();
    }

    [RelayCommand]
    private void Close() => Finished?.Invoke();
}
