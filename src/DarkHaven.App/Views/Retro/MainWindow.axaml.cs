using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using DarkHaven.App.ViewModels;

namespace DarkHaven.App.Views.Retro;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();

        // No OS frame (SystemDecorations="None"): the menu bar moves the window, the edge grips resize it.
        WindowChrome.Attach(this, TitleBar);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanging -= OnPageChanging;
            _vm.PropertyChanged -= OnPageChanged;
        }
        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanging += OnPageChanging;
            _vm.PropertyChanged += OnPageChanged;
        }
    }

    // The view model outlives the window (a theme switch opens another on it).
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        DataContext = null;
    }

    private static bool IsMenu(NavPage page) => page is NavPage.Settings or NavPage.Admin;

    // The page is still the old one here, and the view model already knows the new: a picture of the
    // page being left, if what opens is a menu over it (and not one menu after another).
    private void OnPageChanging(object? sender, PropertyChangingEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.Current) || _vm is null || !IsMenu(_vm.Page) || Backdrop.Source is not null)
            return;
        if (Page.Bounds is not { Width: >= 1, Height: >= 1 } bounds)
            return;

        var scale = RenderScaling;
        var picture = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        picture.Render(Page);
        Backdrop.Source = picture;
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.Current) || _vm is null || IsMenu(_vm.Page))
            return;
        (Backdrop.Source as IDisposable)?.Dispose();
        Backdrop.Source = null;
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
