using Avalonia.Controls;
using DarkHaven.App.ViewModels;

namespace DarkHaven.App.Views;

public partial class ServerListView : UserControl
{
    public ServerListView()
    {
        InitializeComponent();
        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is ServerListViewModel { Servers.Count: 0 } vm)
                await vm.RefreshAsync();
        };
    }
}
