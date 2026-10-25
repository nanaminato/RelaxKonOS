using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public partial class ComponentCenterWorkspace : UserControl
{
    private ComponentCenterViewModel? _model;
    private bool _compact;
    /// <summary>Receives the owning application id of the selected component; the catalog never invents one.</summary>
    public Action<string>? OpenApplication { get; set; }
    public ComponentCenterWorkspace() => InitializeComponent();
    public ComponentCenterWorkspace(ComponentCenterViewModel viewModel) : this() => DataContext = viewModel;
    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_model is not null) _model.PropertyChanged -= ModelChanged;
        base.OnDataContextChanged(e);
        _model = DataContext as ComponentCenterViewModel;
        if (_model is not null) { _model.PropertyChanged += ModelChanged; RuntimeView?.ShowCenterSection(_model.SelectedPage, !_model.ShowTaskDomainProgress); }
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ComponentCenterViewModel.SelectedPage) or nameof(ComponentCenterViewModel.ShowTaskDomainProgress))
            RuntimeView.ShowCenterSection(_model!.SelectedPage, !_model.ShowTaskDomainProgress);
        if (_compact && e.PropertyName is nameof(ComponentCenterViewModel.SelectedPage) or nameof(ComponentCenterViewModel.SelectedNode))
            NavigationToggle.IsChecked = false;
    }
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        DraftFooterScroll.MaxHeight = Math.Max(90, Math.Min(220, e.NewSize.Height * .3));
        var compact = e.NewSize.Width < 840;
        if (compact == _compact) return;
        var toggleFocused = NavigationToggle.IsKeyboardFocusWithin;
        _compact = compact;
        WorkspaceBody.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "260,*");
        Grid.SetColumn(DetailsScroll, compact ? 0 : 1);
        Grid.SetRow(DetailsScroll, compact ? 1 : 0);
        Grid.SetRowSpan(DetailsScroll, compact ? 1 : 2);
        Grid.SetRow(NavigationPane, compact ? 1 : 0);
        Grid.SetRowSpan(NavigationPane, compact ? 1 : 2);
        Grid.SetColumnSpan(NavigationToggle, compact ? 1 : 2);
        NavigationToggle.IsVisible = compact;
        NavigationToggle.IsChecked = false;
        UpdateNavigation();
        if (!compact && toggleFocused) Search.Focus();
    }
    private void NavigationToggle_Changed(object? sender, RoutedEventArgs e) => UpdateNavigation();
    private void UpdateNavigation()
    {
        if (NavigationPane is null || DetailsScroll is null) return;
        var navigationFocused = NavigationPane.IsKeyboardFocusWithin;
        var showNavigation = !_compact || NavigationToggle.IsChecked == true;
        NavigationPane.IsVisible = showNavigation;
        DetailsScroll.IsVisible = !_compact || !showNavigation;
        if (_compact && !showNavigation && navigationFocused) NavigationToggle.Focus();
    }
    private void OpenApplication_Click(object? sender, RoutedEventArgs e)
    {
        if (_model?.SelectedApplicationId is { } application) OpenApplication?.Invoke(application);
    }
    private void RemoveDraft_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ComponentDraftItem item } && _model is { } model) model.RemoveCommand.Execute(item);
    }
}
