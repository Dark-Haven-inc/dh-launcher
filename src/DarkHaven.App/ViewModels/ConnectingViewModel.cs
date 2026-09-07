using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Servers;
using DarkHaven.Launcher.Update;
using Serilog;

namespace DarkHaven.App.ViewModels;

public partial class ConnectingViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly ServerEntry _server;
    private readonly CancellationTokenSource _cts = new();

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _status = "Starting…";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private bool _isBusy = true;
    [ObservableProperty] private string? _errorText;

    public event Action? Finished;

    public ConnectingViewModel(AppServices services, ServerEntry server)
    {
        _services = services;
        _server = server;
        _title = server.DisplayName;
    }

    public async void Start()
    {
        var progress = new Progress<LaunchProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            Status = p.Message;
            if (p.Fraction is { } f)
            {
                IsIndeterminate = false;
                Progress = f * 100;
            }
            else
            {
                IsIndeterminate = true;
            }
        }));

        try
        {
            var compat = _services.Settings.GetConfig("CompatMode") == "true";
            var proc = await _services.Launch.ConnectAsync(_server.Address, allowGuest: true, compat, progress, _cts.Token);

            Status = "Client running";
            IsIndeterminate = false;
            Progress = 100;
            _ = WatchProcessAsync(proc);
        }
        catch (OperationCanceledException)
        {
            Close();
        }
        catch (Exception e)
        {
            Log.Error(e, "Connect failed");
            IsBusy = false;
            IsIndeterminate = false;
            ErrorText = e.Message;
        }
    }

    private async Task WatchProcessAsync(Process proc)
    {
        // Give it a moment; if it dies instantly something is wrong.
        var exitedFast = await Task.WhenAny(proc.WaitForExitAsync(), Task.Delay(1500)) == proc.WaitForExitAsync()
                         || proc.HasExited;

        Dispatcher.UIThread.Post(() =>
        {
            if (proc.HasExited && proc.ExitCode != 0)
            {
                IsBusy = false;
                ErrorText = $"The client exited unexpectedly (code {proc.ExitCode}). See the launcher log.";
            }
            else
            {
                Close();
            }
        });
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
