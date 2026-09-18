using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher;
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
    public bool IsFailed => State == StepState.Failed;

    partial void OnStateChanged(StepState value)
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsFailed));
    }
}

public partial class ConnectingViewModel : ViewModelBase
{
    /// <summary>
    /// A non-zero exit within this long of launch means "the game didn't start" rather than "the
    /// player quit", and brings this card back with the client's own output.
    /// </summary>
    private static readonly TimeSpan EarlyExitWindow = TimeSpan.FromSeconds(60);

    private readonly AppServices _services;
    private readonly ServerEntry _server;
    private readonly CancellationTokenSource _cts = new();
    private ClientLog? _clientLog;
    private bool _shown = true;

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _speedEta = "";
    [ObservableProperty] private string _buildTag = "";
    [ObservableProperty] private string? _motd;
    [ObservableProperty] private bool _isBusy = true;
    [ObservableProperty] private string? _errorText;

    public bool HasError => ErrorText is not null;
    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    /// <summary>The client's last output lines, shown when the game dies right after launch.</summary>
    [ObservableProperty] private string? _logTail;
    [ObservableProperty] private string _copyLabel = "Скопировать лог";

    public bool HasLogTail => LogTail is not null;
    partial void OnLogTailChanged(string? value) => OnPropertyChanged(nameof(HasLogTail));

    public ObservableCollection<ChecklistStepViewModel> Steps { get; } =
    [
        new(LaunchStep.Engine, "Версия движка"),
        new(LaunchStep.Content, "Загрузка контента"),
        new(LaunchStep.Verify, "Проверка файлов"),
        new(LaunchStep.Start, "Запуск игры"),
    ];

    public event Action? Finished;

    /// <summary>
    /// The game died early after this card had already closed — the owner should show it again.
    /// </summary>
    public event Action? Reopen;

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

        // A fresh log per attempt; the previous one moves to client.prev.log, so a retry doesn't
        // wipe the output of the run that actually failed.
        _clientLog?.Dispose();
        _clientLog = new ClientLog(LauncherPaths.ClientLogPath, LauncherPaths.PreviousClientLogPath);

        try
        {
            var compat = _services.Settings.GetConfig("CompatMode") == "true";
            var proc = await _services.Launch.ConnectAsync(
                _server.Address, allowGuest: true, compat, progress,
                onResolved: r => Dispatcher.UIThread.Post(() =>
                {
                    if (r.Info.Desc is { Length: > 0 } d)
                        Motd = d.Trim();
                    if (!_server.IsDarkHavenRegion && r.Info.Desc is null && _server.ServerName is { Length: > 0 } sn)
                        Title = sn;
                }),
                clientLog: _clientLog,
                cancel: _cts.Token);

            try { _services.Settings.RecordRecent(_server.Address, _server.DisplayName, _server.IsDarkHavenRegion); }
            catch (Exception e) { Log.Warning(e, "Could not record recent server"); }

            _services.Discord.SetInGame(_server.DisplayName, _server.IsDarkHavenRegion);
            _services.SetGameSession(_server.Address, _server.DisplayName, _server.IsDarkHavenRegion);
            App.SetGameRunning(true);
            _ = WatchProcessAsync(proc);
        }
        catch (OperationCanceledException)
        {
            _services.Discord.SetIdle();
            _services.SetGameSession(null);
            Close();
        }
        catch (Exception e)
        {
            Log.Error(e, "Connect failed");
            _services.Discord.SetIdle();
            _services.SetGameSession(null);
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
        var sinceLaunch = Stopwatch.StartNew();
        var exited = proc.WaitForExitAsync();

        // Give the client a moment; once it's clearly up, get out of the way. Watching continues —
        // the 0.2.2 loader failure took a few seconds to surface, well after this card had closed.
        await Task.WhenAny(exited, Task.Delay(1500));
        if (!proc.HasExited)
            Dispatcher.UIThread.Post(Close);

        // WaitForExitAsync also waits for the redirected output to drain, so the tail is complete.
        await exited;
        var code = proc.ExitCode;
        var elapsed = sinceLaunch.Elapsed;

        Dispatcher.UIThread.Post(() =>
        {
            _services.Discord.SetIdle();
            _services.SetGameSession(null);
            App.SetGameRunning(false);

            if (code != 0 && elapsed < EarlyExitWindow)
            {
                Log.Error("Game client exited {Code} after {Seconds:0.0}s, see {Log}",
                    ClientLog.FormatExitCode(code), elapsed.TotalSeconds, _clientLog?.Path);
                ShowEarlyExit(code, elapsed);
            }
            else if (_shown)
            {
                Close();
            }

            _clientLog?.Dispose();
        });
    }

    private void ShowEarlyExit(int code, TimeSpan elapsed)
    {
        IsBusy = false;
        SpeedEta = "";

        var start = Steps.First(s => s.Step == LaunchStep.Start);
        start.State = StepState.Failed;
        start.Detail = "игра закрылась";

        ErrorText = $"Игра закрылась через {Math.Max(1, (int)elapsed.TotalSeconds)} с после запуска: " +
                    $"код {ClientLog.FormatExitCode(code)}, {ClientLog.DescribeExitCode(code)}.";
        LogTail = _clientLog?.Tail() is { Length: > 0 } tail
            ? tail
            : "Игра ничего не вывела перед закрытием.";

        if (!_shown)
        {
            _shown = true;
            Reopen?.Invoke();
        }
    }

    [RelayCommand]
    private async Task CopyLog()
    {
        var text = $"Frontier 15 Launcher {LauncherInfo.Version}{Environment.NewLine}" +
                   $"Сервер: {_server.DisplayName} ({_server.Address}){Environment.NewLine}" +
                   $"{ErrorText}{Environment.NewLine}{Environment.NewLine}{LogTail}";
        await App.CopyToClipboardAsync(text);

        CopyLabel = "Скопировано";
        await Task.Delay(2000);
        CopyLabel = "Скопировать лог";
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts.Cancel();
        Close();
    }

    [RelayCommand]
    private void Close()
    {
        _shown = false;
        Finished?.Invoke();
    }

    [RelayCommand]
    private void Retry()
    {
        ErrorText = null;
        LogTail = null;
        foreach (var s in Steps) { s.State = StepState.Pending; s.Detail = ""; s.ShowBar = false; }
        SpeedEta = "";
        IsBusy = true;
        Start();
    }

    [RelayCommand]
    private void OpenLogs()
    {
        try
        {
            DarkHaven.Launcher.LauncherPaths.EnsureDirectories();
            Process.Start(new ProcessStartInfo(DarkHaven.Launcher.LauncherPaths.LogsDir) { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }
}
