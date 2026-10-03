using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DarkHaven.App.Themes;
using DarkHaven.App.ViewModels;

namespace DarkHaven.App.Views.Cyberpunk;

public partial class MainWindow : Window
{
    // The title's glitch: its pink and cyan ghosts jump apart for a moment and settle back, every few
    // seconds, and only while the window is the active one — a launcher left in the background, or behind
    // the game, draws nothing for it. НАСТРОЙКИ → Вид can switch it off (CyberEffects.Glitch).
    private static readonly (double PinkX, double PinkY, double CyanX, double CyanY)[] Glitch =
        [(-4, 1, 3, -1), (3, -1, -3, 0), (-2, 0, 2, 1), (-1.5, 0, 1.5, 0)];

    private readonly DispatcherTimer _glitch;
    private readonly Random _random = new();
    private int _frame = -1;

    public MainWindow()
    {
        InitializeComponent();

        // No OS frame (SystemDecorations="None"): the title bar moves the window, the edge grips resize it.
        WindowChrome.Attach(this, TitleBar);

        _glitch = new DispatcherTimer(NextPause(), DispatcherPriority.Background, (_, _) => OnGlitch());
        _glitch.Start();

        // Glow switched off in НАСТРОЙКИ → Вид: the styles drop every glow under Window.noglow.
        ShowGlow();
        CyberEffects.Changed += ShowGlow;
    }

    private void ShowGlow() => Classes.Set("noglow", !CyberEffects.Glow);

    private TimeSpan NextPause() => TimeSpan.FromSeconds(6 + _random.NextDouble() * 4);

    private void OnGlitch()
    {
        if (_frame < 0 && (!CyberEffects.Glitch || !IsActive || WindowState == WindowState.Minimized))
        {
            _glitch.Interval = NextPause();
            return;
        }

        _frame++;
        var (pinkX, pinkY, cyanX, cyanY) = Glitch[_frame];
        Move(GhostPink, pinkX, pinkY);
        Move(GhostCyan, cyanX, cyanY);

        if (_frame == Glitch.Length - 1)
        {
            _frame = -1; // the last frame is the title at rest
            _glitch.Interval = NextPause();
        }
        else
        {
            _glitch.Interval = TimeSpan.FromMilliseconds(55);
        }
    }

    private static void Move(Visual ghost, double x, double y)
    {
        if (ghost.RenderTransform is TranslateTransform t)
        {
            t.X = x;
            t.Y = y;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _glitch.Stop();
        CyberEffects.Changed -= ShowGlow;
        base.OnClosed(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnPaletteBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only a click on the dimmed backdrop itself, not inside the switcher.
        if (e.Source == sender && DataContext is MainWindowViewModel vm)
            vm.ClosePalette();
    }
}
