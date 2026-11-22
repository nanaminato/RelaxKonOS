using System.ComponentModel;
using Avalonia.Controls;
using RelaxKonOS.App.ComponentCenter;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

/// <summary>Presentation only: closing this dialog neither cancels work nor discards drafts.</summary>
public partial class ComponentCenterTasksView : UserControl
{
    private ComponentCenterViewModel? _model;
    private ComponentCenterTasksView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (_model is not null) { _model.PropertyChanged -= ModelChanged; _model.PropertyChanged += ModelChanged; }
            UpdateRuntime();
        };
        DetachedFromVisualTree += (_, _) => { if (_model is not null) _model.PropertyChanged -= ModelChanged; };
    }
    public ComponentCenterTasksView(ComponentCenterViewModel model, Action? requestClose = null)
        : this(model, requestClose, ComponentTaskDialogLayout.Create, () => new ComponentTaskPlanningView()) { }

    internal ComponentCenterTasksView(ComponentCenterViewModel model, Action? requestClose,
        Func<ComponentTaskDialogPresentation, Control> createLayout, Func<Control> createPlanning) : this()
    {
        Planning.Content = createPlanning();
        var body = (Control)Content!;
        Content = null;
        DataContext = model;
        Content = createLayout(new(model, body, requestClose));
    }

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
}
