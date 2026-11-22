using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public partial class ComponentCenterWorkspace : UserControl
{
    private ComponentCenterViewModel? _model;
    /// <summary>Receives the owning application id of the selected component; the catalog never invents one.</summary>
    public Action<string>? OpenApplication { get; set; }
    public ComponentCenterWorkspace() => InitializeComponent();
    public ComponentCenterWorkspace(ComponentCenterViewModel viewModel) : this() => DataContext = viewModel;
    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_model is not null) _model.PropertyChanged -= ModelChanged;
        base.OnDataContextChanged(e);
        _model = DataContext as ComponentCenterViewModel;
        if (_model is not null) { _model.PropertyChanged += ModelChanged; RuntimeView?.ShowCenterSection(_model.SelectedPage, false); }
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ComponentCenterViewModel.SelectedPage) or nameof(ComponentCenterViewModel.ShowTaskDomainProgress))
            RuntimeView.ShowCenterSection(_model!.SelectedPage, false);
        if (e.PropertyName is nameof(ComponentCenterViewModel.SelectedPage) or nameof(ComponentCenterViewModel.SelectedNode))
            WorkspaceLayout.CloseCompactNavigation();
    }
    private void OpenApplication_Click(object? sender, RoutedEventArgs e)
    {
        if (_model?.SelectedApplicationId is { } application) OpenApplication?.Invoke(application);
    }
}
