using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
using Avalonia.Input;
using RelaxKonOS.Client.Apps.Settings.ViewModels;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.Settings.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _observedModel;
    private readonly Dictionary<string, Vector> _scrollOffsets = new();
    private int _navigationGeneration;
    private bool _wasSearching;
    private SettingsSearchHighlight? _highlight;
    private readonly DispatcherTimer _highlightTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public SettingsView()
    {
        InitializeComponent();
        SettingsHeader.BeforeFocusSearch = () => CloseNavigationDrawer(restoreFocus: false);
        _highlightTimer.Tick += (_, _) => ClearHighlight();
        DataContextChanged += (_, _) => ObserveModel(DataContext as SettingsViewModel);
        AttachedToVisualTree += (_, _) => ObserveModel(DataContext as SettingsViewModel);
        DetachedFromVisualTree += (_, _) => { ObserveModel(null); ClearHighlight(); };

        SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width < 760;
            NavigationLayout.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 250);
            Sidebar.IsVisible = !compact;
            CompactNavigation.IsVisible = compact;
            if (!compact) CloseNavigationDrawer(restoreFocus: false);
        };
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && NavigationDrawer.IsVisible)
            { CloseNavigationDrawer(); args.Handled = true; }
            else if (args.Key == Key.F && args.KeyModifiers.HasFlag(KeyModifiers.Control))
            { SettingsHeader.FocusSearch(); args.Handled = true; }
            else if (args.Key == Key.Escape && DataContext is SettingsViewModel { HasSearch: true } model)
            { model.SearchQuery = ""; args.Handled = true; }
        };
    }

    public SettingsHeaderView Header => SettingsHeader;
    public void AttachWindowHeader(RelaxKonOS.WindowManager.ManagedWindow window)
    {
        HeaderHost.Content = null;
        SettingsHeader.DataContext = DataContext;
        window.View.TitleBarContent = SettingsHeader;
    }

    private void ObserveModel(SettingsViewModel? model)
    {
        if (ReferenceEquals(model, _observedModel)) return;
        if (_observedModel is { } previous)
        {
            previous.PropertyChanging -= OnModelPropertyChanging;
            previous.PropertyChanged -= OnModelPropertyChanged;
            previous.SearchResultOpened -= OnSearchResultOpened;
            previous.NavigationContextReset -= ResetNavigationContext;
        }
        ResetNavigationContext();
        _observedModel = model;
        if (model is not null)
        {
            model.PropertyChanging += OnModelPropertyChanging;
            model.PropertyChanged += OnModelPropertyChanged;
            model.SearchResultOpened += OnSearchResultOpened;
            model.NavigationContextReset += ResetNavigationContext;
        }
    }

    private ScrollViewer PageScroll => SettingsPageScroll;
    private void OnModelPropertyChanging(object? sender, PropertyChangingEventArgs args)
    {
        if (_observedModel is not { HasSearch: false, SelectedPage: { } page } model) return;
        if (args.PropertyName == nameof(SettingsViewModel.SelectedPage)
            || args.PropertyName == nameof(SettingsViewModel.SearchQuery) && !model.IsRestoringNavigation)
            _scrollOffsets[model.CurrentNavigationRoute] = PageScroll.Offset;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SettingsViewModel.HasSearch) && _observedModel is { } model)
        {
            if (model.HasSearch == _wasSearching) return;
            _wasSearching = model.HasSearch;
            if (model.HasSearch)
            {
                ClearHighlight();
                var searchGeneration = ++_navigationGeneration;
                Dispatcher.UIThread.Post(() =>
                {
                    if (searchGeneration != _navigationGeneration || _observedModel?.HasSearch != true) return;
                    SettingsHeader.FocusSearch();
                }, DispatcherPriority.Loaded);
                return;
            }
            // Escape returns to the pre-search position. A result reveal supersedes this callback.
        }
        else if (args.PropertyName != nameof(SettingsViewModel.SelectedPage)) return;
        ClearHighlight();
        var generation = ++_navigationGeneration;
        var route = _observedModel?.CurrentNavigationRoute;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _navigationGeneration || route != _observedModel?.CurrentNavigationRoute) return;
            if (PageScroll is { } scroll) scroll.Offset = route is not null && _scrollOffsets.TryGetValue(route, out var offset) ? offset : default;
        }, DispatcherPriority.Loaded);
    }

    private void OnSearchResultOpened(SettingsSearchEntry entry)
    {
        ClearHighlight();
        var generation = ++_navigationGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _navigationGeneration || _observedModel is not { } model || model.SelectedPage?.Route != entry.Route || model.HasSearch) return;
            if (entry.SettingId.StartsWith("page.", StringComparison.Ordinal)) return;
            var target = SettingsSearchTarget.Find(PageContent, entry.SettingId);
            model.SearchLocationStatus = target is null
                ? LocalizedText.Get("settings.search.location_unavailable")
                : LocalizedText.Format("settings.search.located", entry.Title);
            if (target is null) return;
            _highlight = SettingsSearchTarget.HighlightAndFocus(target, entry.Title);
            _highlightTimer.Start();
        }, DispatcherPriority.Loaded);
    }

    private void ResetNavigationContext()
    {
        ++_navigationGeneration;
        _scrollOffsets.Clear();
        _wasSearching = false;
        ClearHighlight();
        CloseNavigationDrawer(restoreFocus: false);
    }

    private void ClearHighlight()
    {
        _highlightTimer.Stop();
        _highlight?.Dispose();
        _highlight = null;
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
        if (sender is Button { Tag: string route } && DataContext is SettingsViewModel model)
        {
            var fromDrawer = NavigationDrawer.IsVisible;
            model.OpenPageCommand.Execute(route);
            CloseNavigationDrawer(restoreFocus: false);
            if (fromDrawer) PageHeading.Focus();
        }
    }

    private void OnSearchResultDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs args) => OpenSelectedResult(sender);

    private void OnSearchResultKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter) return;
        OpenSelectedResult(sender);
        args.Handled = true;
    }

    private void OpenSelectedResult(object? sender)
    {
        if (sender is ListBox { SelectedItem: SettingsSearchEntry entry } list && DataContext is SettingsViewModel model)
        {
            list.SelectedItem = null;
            model.OpenSearchResultCommand.Execute(entry);
        }
    }
}
