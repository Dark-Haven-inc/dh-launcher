using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DarkHaven.App.ViewModels;
using DarkHaven.App.Views;

namespace DarkHaven.App;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Services = new AppServices();
            desktop.ShutdownRequested += (_, _) => Services.Dispose();

            var vm = new MainWindowViewModel(Services);
            desktop.MainWindow = new MainWindow { DataContext = vm };

            SingleInstance.StartListening(msg => Dispatcher.UIThread.Post(() => HandleForwarded(msg)));

            if (Program.LaunchUri is { } uri)
                Connect(vm, uri, Program.IsRedial);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>A second launch forwarded us its argument — surface the window and act on it.</summary>
    private static void HandleForwarded(string msg)
    {
        if (MainWindow() is not { } w)
            return;

        var redial = msg.StartsWith("redial\n");
        if (redial)
            msg = msg["redial\n".Length..];

        if (w.WindowState == WindowState.Minimized)
            w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = true;
        w.Topmost = false;

        if ((msg.StartsWith("ss14://") || msg.StartsWith("ss14s://")) && w.DataContext is MainWindowViewModel vm)
            Connect(vm, msg, redial);
    }

    /// <summary>On a redial the previous client is still tearing down — give it a moment before reconnecting.</summary>
    private static void Connect(MainWindowViewModel vm, string address, bool redial)
    {
        if (redial)
            _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.UIThread.Post(() => vm.ConnectToAddress(address)));
        else
            vm.ConnectToAddress(address);
    }

    /// <summary>Minimise while the game runs (if the setting is on); restore + focus when it exits.</summary>
    public static void SetGameRunning(bool running)
    {
        if (MainWindow() is not { } w)
            return;

        if (running)
        {
            if (Services.Settings.GetConfig("MinimizeOnLaunch") != "false")
                w.WindowState = WindowState.Minimized;
        }
        else
        {
            if (w.WindowState == WindowState.Minimized)
                w.WindowState = WindowState.Normal;
            w.Activate();
        }
    }

    private static Window? MainWindow() =>
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
