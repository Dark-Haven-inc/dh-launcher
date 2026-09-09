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
                vm.ConnectToAddress(uri);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>A second launch forwarded us its argument — surface the window and act on it.</summary>
    private static void HandleForwarded(string msg)
    {
        if (MainWindow() is not { } w)
            return;

        if (w.WindowState == WindowState.Minimized)
            w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = true;
        w.Topmost = false;

        if ((msg.StartsWith("ss14://") || msg.StartsWith("ss14s://")) && w.DataContext is MainWindowViewModel vm)
            vm.ConnectToAddress(msg);
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
