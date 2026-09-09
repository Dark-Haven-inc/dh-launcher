using Avalonia.Controls;
using DarkHaven.App.ViewModels;

namespace DarkHaven.App.Views;

public partial class RegionsView : UserControl
{
    public RegionsView()
    {
        InitializeComponent();

        Map.NodeInvoked += (_, node) =>
        {
            if (DataContext is RegionsViewModel vm && node is RegionNodeViewModel rn)
                vm.SelectFromMap(rn);
        };
    }
}
