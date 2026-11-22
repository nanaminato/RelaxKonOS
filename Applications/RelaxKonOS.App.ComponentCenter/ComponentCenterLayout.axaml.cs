using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.App.ComponentCenter;

public partial class ComponentCenterLayout : UserControl
{
    public static readonly StyledProperty<Control?> DetailsProperty =
        AvaloniaProperty.Register<ComponentCenterLayout, Control?>(nameof(Details));

    private bool _compact;

    public ComponentCenterLayout() => InitializeComponent();

    public Control? Details
    {
        get => GetValue(DetailsProperty);
        set => SetValue(DetailsProperty, value);
    }

    public void CloseCompactNavigation()
    {
        if (_compact)
            NavigationToggle.IsChecked = false;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var compact = e.NewSize.Width < 840;
        if (compact == _compact) return;
        var toggleFocused = NavigationToggle.IsKeyboardFocusWithin;
        _compact = compact;
        WorkspaceBody.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "288,*");
        Grid.SetColumn(DetailsScroll, compact ? 0 : 1);
        Grid.SetRow(DetailsScroll, compact ? 1 : 0);
        Grid.SetRowSpan(DetailsScroll, compact ? 1 : 2);
        Grid.SetRow(NavigationPane, compact ? 1 : 0);
        Grid.SetRowSpan(NavigationPane, compact ? 1 : 2);
        Grid.SetColumnSpan(NavigationToggle, compact ? 1 : 2);
        NavigationToggle.IsVisible = compact;
        NavigationToggle.IsChecked = false;
        UpdateNavigation();
        if (!compact && toggleFocused) Navigation.FocusSearch();
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
}

public interface IComponentCenterLayout
{
    string HostIdentity { get; }
    string ConnectionText { get; }
    string HostTargetText { get; }
    bool HasTargetText { get; }
    string HeaderOutcomeText { get; }
    bool HasHeaderOutcome { get; }
}
