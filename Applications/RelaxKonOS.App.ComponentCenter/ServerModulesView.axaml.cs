using Avalonia;
using Avalonia.Controls;

namespace RelaxKonOS.App.ComponentCenter;

public partial class ServerModulesView : UserControl
{
    private bool _compact;
    private bool _layoutApplied;

    public ServerModulesView()
    {
        InitializeComponent();
        PageHeader.Content = new ServerModulesHeaderView();
        Packages.Content = new ServerModulePackagesView();
        Store.Content = new ServerModuleStoreView();
        Security.Content = new ServerModuleSecurityView();
    }

    /// <summary>
    /// A module rail beside a detail pane needs roughly 900 px. Below that the two panes would each
    /// be too narrow to read a module id and its facts, so the list moves above the detail instead
    /// of shrinking both. The component-center workspace already removes its own navigation at the
    /// same width, which is why this page can use the full body width in compact mode.
    /// </summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyLayout(e.NewSize.Width);
    }

    private void ApplyLayout(double width)
    {
        if (width <= 0 || ModuleRail is null || ModuleDetail is null || ModulesLayout is null) return;
        var compact = width < 900;
        if (_layoutApplied && compact == _compact) return;
        _layoutApplied = true;
        _compact = compact;

        ModulesLayout.ColumnDefinitions.Clear();
        ModulesLayout.RowDefinitions.Clear();
        if (compact)
        {
            ModulesLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ModulesLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            ModulesLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(ModuleRail, 0);
            Grid.SetRow(ModuleRail, 0);
            Grid.SetColumn(ModuleDetail, 0);
            Grid.SetRow(ModuleDetail, 1);
            ModuleList.MaxHeight = 360;
        }
        else
        {
            ModulesLayout.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(316)));
            ModulesLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ModulesLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(ModuleRail, 0);
            Grid.SetRow(ModuleRail, 0);
            Grid.SetColumn(ModuleDetail, 1);
            Grid.SetRow(ModuleDetail, 0);
            ModuleList.MaxHeight = 500;
        }
    }
}
