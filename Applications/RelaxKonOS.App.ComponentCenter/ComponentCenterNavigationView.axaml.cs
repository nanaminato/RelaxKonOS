using Avalonia.Controls;

namespace RelaxKonOS.App.ComponentCenter;

public partial class ComponentCenterNavigationView : UserControl
{
    public ComponentCenterNavigationView() => InitializeComponent();

    public void FocusSearch() => Search.Focus();
}

public interface IComponentCenterNavigation
{
    bool ShowComponentBrowser { get; }
    bool HasResults { get; }
    string Search { get; set; }
    IEnumerable<IComponentCenterPageItem> BrowsePages { get; }
    IComponentCenterPageItem? SelectedNavigation { get; set; }
    IEnumerable<IComponentCenterTreeItem> VisibleRoots { get; }
    IComponentCenterTreeItem? SelectedNode { get; set; }
}

public interface IComponentCenterPageItem
{
    string Label { get; }
}

public interface IComponentCenterTreeItem
{
    string Label { get; }
    IReadOnlyList<IComponentCenterTreeItem> VisibleChildren { get; }
    bool ShowStatus { get; }
    bool StatusIdle { get; }
    bool StatusOk { get; }
    bool StatusWarning { get; }
    bool StatusDanger { get; }
    string Status { get; }
    bool ShowVersion { get; }
    string Version { get; }
    bool HasUpdate { get; }
    bool ShowRequirement { get; }
    string Requirement { get; }
}
