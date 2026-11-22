using System.Windows.Input;
using Avalonia.Controls;

namespace RelaxKonOS.App.ComponentCenter;

public partial class ComponentCenterHeaderActions : UserControl
{
    public ComponentCenterHeaderActions() => InitializeComponent();
}

public interface IComponentCenterHeaderActions
{
    ICommand OpenTasksCommand { get; }
    ICommand RefreshCommand { get; }
    IComponentCenterRefreshState Runtime { get; }
}

public interface IComponentCenterRefreshState
{
    bool CanRefreshSources { get; }
}
