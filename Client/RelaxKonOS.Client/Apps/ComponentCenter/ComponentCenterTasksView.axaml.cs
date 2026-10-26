using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

/// <summary>Presentation only: closing this dialog neither cancels work nor discards drafts.</summary>
public partial class ComponentCenterTasksView : UserControl
{
    private ComponentCenterViewModel? _model;
    private readonly Action? _requestClose;
    public ComponentCenterTasksView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (_model is not null) { _model.PropertyChanged -= ModelChanged; _model.PropertyChanged += ModelChanged; }
            UpdateRuntime();
        };
        DetachedFromVisualTree += (_, _) => { if (_model is not null) _model.PropertyChanged -= ModelChanged; };
    }
    public ComponentCenterTasksView(ComponentCenterViewModel model, Action? requestClose = null) : this()
    { DataContext = model; _requestClose = requestClose; }
    private void Close_Click(object? sender, RoutedEventArgs e) => _requestClose?.Invoke();
    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_model is not null) _model.PropertyChanged -= ModelChanged;
        base.OnDataContextChanged(e);
        _model = DataContext as ComponentCenterViewModel;
        if (_model is not null) _model.PropertyChanged += ModelChanged;
        UpdateRuntime();
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ComponentCenterViewModel.DetailRuntime) or nameof(ComponentCenterViewModel.ShowTaskDomainProgress)) UpdateRuntime();
    }
    private void UpdateRuntime() => TaskRuntimeView?.ShowCenterSection(ComponentCenterPage.Tasks, _model?.ShowTaskDomainProgress != true);
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        DraftFooterScroll.MaxHeight = Math.Max(90, Math.Min(220, e.NewSize.Height * .3));
    }
    private void RemoveDraft_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ComponentDraftItem item } && _model is { } model) model.RemoveCommand.Execute(item);
    }
}
