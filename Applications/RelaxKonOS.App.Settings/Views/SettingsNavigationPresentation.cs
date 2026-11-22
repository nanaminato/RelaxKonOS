using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace RelaxKonOS.App.Settings.Views;

public interface ISettingsNavigationPresentation : INotifyPropertyChanging, INotifyPropertyChanged
{
    SettingsPageModel? SelectedPage { get; }
    string CurrentNavigationRoute { get; }
    string SearchQuery { get; set; }
    Func<string, string> Labels { get; }
    bool HasSearch { get; }
    bool IsRestoringNavigation { get; }
    string SearchLocationStatus { get; set; }
    event Action<SettingsSearchEntry>? SearchResultOpened;
    event Action? NavigationContextReset;
}

public sealed class SettingsNavigationPresentation
{
    private readonly ScrollViewer scroll;
    private readonly Control pageContent;
    private readonly Action focusSearch;
    private readonly Action closeDrawer;
    private readonly Func<string, string> labels;

    public SettingsNavigationPresentation(ScrollViewer scroll, Control pageContent, Action focusSearch,
        Action closeDrawer, Func<string, string> labels)
    {
        this.scroll = scroll;
        this.pageContent = pageContent;
        this.focusSearch = focusSearch;
        this.closeDrawer = closeDrawer;
        this.labels = labels;
        _highlightTimer.Tick += (_, _) => ClearHighlight();
    }

    private ISettingsNavigationPresentation? _observedModel;
    private readonly Dictionary<string, Vector> _scrollOffsets = new();
    private int _navigationGeneration;
    private bool _wasSearching;
    private SettingsSearchHighlight? _highlight;
    private readonly DispatcherTimer _highlightTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public void Observe(ISettingsNavigationPresentation? model)
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

    private void OnModelPropertyChanging(object? sender, PropertyChangingEventArgs args)
    {
        if (_observedModel is not { HasSearch: false, SelectedPage: not null } model) return;
        if (args.PropertyName == nameof(ISettingsNavigationPresentation.SelectedPage)
            || args.PropertyName == nameof(ISettingsNavigationPresentation.SearchQuery) && !model.IsRestoringNavigation)
            _scrollOffsets[model.CurrentNavigationRoute] = scroll.Offset;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ISettingsNavigationPresentation.HasSearch) && _observedModel is { } model)
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
                    focusSearch();
                }, DispatcherPriority.Loaded);
                return;
            }
            // Escape returns to the pre-search position. A result reveal supersedes this callback.
        }
        else if (args.PropertyName != nameof(ISettingsNavigationPresentation.SelectedPage)) return;
        ClearHighlight();
        var generation = ++_navigationGeneration;
        var route = _observedModel?.CurrentNavigationRoute;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _navigationGeneration || route != _observedModel?.CurrentNavigationRoute) return;
            scroll.Offset = route is not null && _scrollOffsets.TryGetValue(route, out var offset) ? offset : default;
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
            var target = SettingsSearchTarget.Find(pageContent, entry.SettingId);
            model.SearchLocationStatus = target is null
                ? labels("settings.search.location_unavailable")
                : string.Format(labels("settings.search.located"), entry.Title);
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
        closeDrawer();
    }

    public void ClearHighlight()
    {
        _highlightTimer.Stop();
        _highlight?.Dispose();
        _highlight = null;
    }

}
