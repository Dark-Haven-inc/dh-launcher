using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

            var vm = new MainWindowViewModel(Services);
            desktop.MainWindow = new MainWindow { DataContext = vm };

            if (Program.LaunchUri is { } uri)
                vm.ConnectToAddress(uri);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
