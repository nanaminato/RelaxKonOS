using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace RelaxKonOS.App.Settings.Views;

public partial class SettingsWorkspaceView : UserControl
{
    private readonly SettingsNavigationPresentation _navigation;

    public SettingsWorkspaceView()
    {
        InitializeComponent();
        SettingsSearchBar.BeforeFocusSearch = () => CloseNavigationDrawer(restoreFocus: false);
        // The window's title bar is not inside this view once it is fused, so the shortcut keys are
        // attached to both blocks instead of being handled by the content view alone.
        SettingsHeader.KeyDown += OnTitleBarKeyDown;
        SettingsSearchBar.KeyDown += OnTitleBarKeyDown;
        _navigation = new(SettingsPageScroll, PageContent, () => SettingsSearchBar.FocusSearch(),
            () => CloseNavigationDrawer(restoreFocus: false), key => (DataContext as ISettingsNavigationPresentation)?.Labels(key) ?? key);
        DataContextChanged += (_, _) => _navigation.Observe(DataContext as ISettingsNavigationPresentation);
        AttachedToVisualTree += (_, _) => _navigation.Observe(DataContext as ISettingsNavigationPresentation);
        DetachedFromVisualTree += (_, _) => { _navigation.Observe(null); _navigation.ClearHighlight(); };

        SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width < 760;
            NavigationLayout.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 250);
            Sidebar.IsVisible = !compact;
            CompactNavigation.IsVisible = compact;
            SettingsHeader.TitleText.IsVisible = args.NewSize.Width >= 360;
            if (!compact) CloseNavigationDrawer(restoreFocus: false);
        };
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && NavigationDrawer.IsVisible)
            { CloseNavigationDrawer(); args.Handled = true; }
            else if (args.Key == Key.F && args.KeyModifiers.HasFlag(KeyModifiers.Control))
            { SettingsSearchBar.FocusSearch(); args.Handled = true; }
            else if (args.Key == Key.Escape && DataContext is ISettingsNavigationPresentation { HasSearch: true } model)
            { model.SearchQuery = ""; args.Handled = true; }
        };
    }

    private void OnTitleBarKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Handled) return;
        if (args.Key == Key.F && args.KeyModifiers.HasFlag(KeyModifiers.Control))
        { SettingsSearchBar.FocusSearch(); args.Handled = true; }
        else if (args.Key == Key.Escape && DataContext is ISettingsNavigationPresentation { HasSearch: true } model)
        { model.SearchQuery = ""; args.Handled = true; }
    }

    /// <summary>The leading title-bar block: the back action and this window's own name.</summary>
    public SettingsHeaderView Header => SettingsHeader;

    /// <summary>The centre title-bar block: the search field.</summary>
    public SettingsSearchBarView SearchBar => SettingsSearchBar;

    /// <summary>
    /// Hands the two title-bar blocks to the host's role slots. This is the whole application-side
    /// contract: no view names a column, an inset or a bar height, so all three window-chrome
    /// recipes - and any recipe the host adds later - place them without this file changing.
    /// </summary>
    public void AttachWindowHeader(RelaxKonOS.WindowManager.ManagedWindow window)
    {
        SettingsHeader.DataContext = DataContext;
        SettingsSearchBar.DataContext = DataContext;
        HeaderHost.Children.Clear();
        window.View.HeaderLeading = SettingsHeader;
        window.View.HeaderCenter = SettingsSearchBar;
        window.View.HeaderTrailing = null;
    }

    private void OnOpenNavigationClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        NavigationDrawer.IsVisible = true;
        CloseNavigationButton.Focus();
    }

    private void OnCloseNavigationClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => CloseNavigationDrawer();

    private void CloseNavigationDrawer(bool restoreFocus = true)
    {
        if (!NavigationDrawer.IsVisible) return;
        NavigationDrawer.IsVisible = false;
        if (restoreFocus && NavigationMenuButton.IsEffectivelyVisible) NavigationMenuButton.Focus();
    }

    private void OnQuickLinkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (sender is Button { Tag: string route })
            NavigateTo(route);
    }

    public void NavigateTo(string route)
    {
        if (DataContext is ISettingsWorkspace model)
        {
            var fromDrawer = NavigationDrawer.IsVisible;
            model.OpenPageCommand.Execute(route);
            CloseNavigationDrawer(restoreFocus: false);
            if (fromDrawer) PageHeading.Focus();
        }
    }

}
