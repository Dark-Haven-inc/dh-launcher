using System;
using System.Runtime.InteropServices;
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

    public static async Task CopyToClipboardAsync(string text)
    {
        if (MainWindow()?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    /// <summary>Draw attention to the launcher when something happens while it's in the background
    /// (a watched region came online). Flashes the taskbar button on Windows; elsewhere just
    /// un-minimises so the in-window banner is visible.</summary>
    public static void AlertUser()
    {
        if (MainWindow() is not { } w)
            return;

        if (OperatingSystem.IsWindows() && !w.IsActive)
        {
            try
            {
                if (w.TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != IntPtr.Zero)
                {
                    var info = new FLASHWINFO
                    {
                        cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                        hwnd = hwnd,
                        dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                        uCount = 3,
                        dwTimeout = 0,
                    };
                    FlashWindowEx(ref info);
                }
            }
            catch { /* cosmetic only */ }
        }
    }

    private const uint FLASHW_ALL = 0x00000003;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    private static Window? MainWindow() =>
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
