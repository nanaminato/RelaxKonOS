using Avalonia.Controls;

namespace RelaxKonOS.App.ComponentCenter;

public partial class ComponentCenterEmptyView : UserControl
{
    public ComponentCenterEmptyView() => InitializeComponent();
}

public interface IComponentCenterEmptyState
{
    string EmptyMessage { get; }
}
