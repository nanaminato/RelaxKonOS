using Avalonia.Controls;

namespace RelaxKonOS.App.ComponentCenter;

public partial class ComponentCenterNavigationView : UserControl
{
    public ComponentCenterNavigationView() => InitializeComponent();

    public void FocusSearch() => Search.Focus();
}
