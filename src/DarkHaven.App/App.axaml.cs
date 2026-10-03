using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DarkHaven.App.Themes;
using DarkHaven.App.ViewModels;

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
            RetroEffects.Load(Services.Settings);
            CyberEffects.Load(Services.Settings);
            desktop.ShutdownRequested += (_, _) => Services.Dispose();

            var theme = Theme.Find(Services.Settings.GetConfig("Theme"));
            Theme.Apply(this, theme, theme.LoadColors(Services.Settings));
            var vm = new MainWindowViewModel(Services);
            var window = Theme.Current.CreateMainWindow();
            window.DataContext = vm;
            desktop.MainWindow = window;

            // Kick off first loads once the window is shown (only this first one — a theme switch
            // replaces the window, not the data).
            window.Opened += async (_, _) => await vm.Regions.RefreshAsync();

            SingleInstance.StartListening(msg => Dispatcher.UIThread.Post(() => HandleForwarded(msg)));

            if (Program.LaunchUri is { } uri)
                Connect(vm, uri, Program.IsRedial);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Switches the look while running: the new theme's palette and styles, then a fresh main window
    /// of its layout on the same view model, where the old one was. Views pick their resources up
    /// once, when they load, so restyling the open window in place isn't an option.
    /// </summary>
    public static void SwitchTheme(Theme theme)
    {
        if (theme == Theme.Current || Current is null
            || Current.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } old } desktop)
            return;

        Theme.Apply(Current, theme, theme.LoadColors(Services.Settings));

        var window = theme.CreateMainWindow();
        window.DataContext = old.DataContext;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = old.Position;
        if (old.WindowState == WindowState.Normal)
        {
            // The layouts have different minimum sizes.
            window.Width = Math.Max(old.ClientSize.Width, window.MinWidth);
            window.Height = Math.Max(old.ClientSize.Height, window.MinHeight);
        }
        else
        {
            window.WindowState = old.WindowState;
        }

        desktop.MainWindow = window;
        window.Show();
        old.Close();
    }

    /// <summary>The player's colors on the current theme, applied live (the color editor in НАСТРОЙКИ).</summary>
    public static void Recolor(ThemeColors colors)
    {
        if (Current is not null)
            Theme.Recolor(Current, colors);
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
