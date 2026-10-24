using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public partial class ComponentCenterWorkspace : UserControl
{
    private ComponentCenterViewModel? _model;
    public Action? OpenApplication { get; set; }
    public ComponentCenterWorkspace() => InitializeComponent();
    public ComponentCenterWorkspace(ComponentCenterViewModel viewModel) : this() => DataContext = viewModel;
    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_model is not null) _model.PropertyChanged -= ModelChanged;
        base.OnDataContextChanged(e);
        _model = DataContext as ComponentCenterViewModel;
        if (_model is not null) { _model.PropertyChanged += ModelChanged; RuntimeView?.ShowCenterSection(_model.SelectedPage); }
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(ComponentCenterViewModel.SelectedPage)) RuntimeView.ShowCenterSection(_model!.SelectedPage); }
    private void OpenApplication_Click(object? sender, RoutedEventArgs e) => OpenApplication?.Invoke();
    private void RemoveDraft_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ComponentDraftItem item } && _model is { } model) model.RemoveCommand.Execute(item);
    }
}
