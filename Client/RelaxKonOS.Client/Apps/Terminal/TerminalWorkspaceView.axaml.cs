using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>Retains each session view in the visual tree while switching the visible tab.</summary>
public partial class TerminalWorkspaceView : UserControl
{
    /// <summary>Distance one wheel notch travels along a strip that only scrolls sideways.</summary>
    private const double TabWheelStep = 60;

    private readonly Dictionary<TerminalViewModel, TerminalView> _views = [];
    private bool _syncingPicker;
    public ObservableCollection<TerminalViewModel> Tabs { get; } = [];
    public Action? RequestNewTab { get; set; }
    public Func<TerminalViewModel, Task>? RequestCloseTabAsync { get; set; }
    public TerminalWorkspaceView()
    {
        InitializeComponent();
        TabStrip.ItemsSource = Tabs;
        TabStrip.SelectionChanged += (_, _) => UpdateSelection();
        TabPicker.ItemsSource = Tabs;
        // Tunnelling on the strip's own scroller keeps the wheel inside the tab row: the session
        // below must keep scrolling its scrollback while the pointer never leaves the terminal.
        TabScroller.AddHandler(PointerWheelChangedEvent, OnTabStripWheel, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnWorkspaceKeyDown, RoutingStrategies.Tunnel);
    }

    public void AddTab(TerminalViewModel model, TerminalView view)
    {
        _views.Add(model, view);
        // Hide other controls before adding: inactive sessions keep their connection and scrollback.
        foreach (var existing in _views.Values) existing.IsVisible = false;
        SessionHost.Children.Add(view);
        Tabs.Add(model);
        TabStrip.SelectedItem = model;
    }

    public void RemoveTab(TerminalViewModel model)
    {
        if (!_views.Remove(model, out var view)) return;
        var index = Tabs.IndexOf(model);
        var selected = ReferenceEquals(TabStrip.SelectedItem, model);
        SessionHost.Children.Remove(view);
        Tabs.Remove(model);
        if (selected && Tabs.Count > 0) TabStrip.SelectedItem = Tabs[Math.Min(index, Tabs.Count - 1)];
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        foreach (var (model, view) in _views)
            view.IsVisible = ReferenceEquals(TabStrip.SelectedItem, model);
        if (TabStrip.SelectedItem is TerminalViewModel selected)
        {
            if (_views.TryGetValue(selected, out var active)) active.FocusSession();
            // A keyboard switch must never leave the newly active session scrolled out of sight.
            // Background runs after the strip has been laid out, so the offsets it reads are real.
            Dispatcher.UIThread.Post(() => ScrollTabIntoView(selected), DispatcherPriority.Background);
        }
    }

    private void ScrollTabIntoView(TerminalViewModel model)
    {
        if (TabStrip.ContainerFromItem(model) is not Control container) return;
        var extent = TabScroller.Extent.Width;
        var viewport = TabScroller.Viewport.Width;
        if (extent <= viewport || container.TranslatePoint(default, TabScroller) is not { } origin) return;
        var offset = TabScroller.Offset.X;
        // The + rides after the last session, so standing on that session has to bring the row's own
        // end into the window too; scrolling to the tab's edge alone would leave the + just past it.
        if (Tabs.Count > 0 && ReferenceEquals(Tabs[^1], model)) offset = extent;
        else if (origin.X < 0) offset += origin.X;
        else if (origin.X + container.Bounds.Width > viewport) offset += origin.X + container.Bounds.Width - viewport;
        TabScroller.Offset = TabScroller.Offset.WithX(Math.Clamp(offset, 0, extent - viewport));
    }

    /// <summary>Scrolls the strip by whole wheel notches; reports whether it actually moved.</summary>
    internal bool ScrollTabsBy(double notches)
    {
        var range = TabScroller.Extent.Width - TabScroller.Viewport.Width;
        if (range <= 0) return false;
        var next = Math.Clamp(TabScroller.Offset.X - notches * TabWheelStep, 0, range);
        if (next == TabScroller.Offset.X) return false;
        TabScroller.Offset = TabScroller.Offset.WithX(next);
        return true;
    }

    private void OnTabStripWheel(object? sender, PointerWheelEventArgs e)
    {
        if (ScrollTabsBy(e.Delta.Y)) e.Handled = true;
    }

    private void TabPicker_Opening(object? sender, EventArgs e)
    {
        // Seeding the picker with the active session must not read as a user selection, or the
        // picker would close itself the moment it opens.
        _syncingPicker = true;
        try { TabPicker.SelectedItem = TabStrip.SelectedItem; }
        finally { _syncingPicker = false; }
    }

    private void TabPicker_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingPicker || TabPicker.SelectedItem is not TerminalViewModel model) return;
        TabStrip.SelectedItem = model;
        TabPickerButton.Flyout?.Hide();
    }

    private void AddTab_Click(object? sender, RoutedEventArgs e) => RequestNewTab?.Invoke();
    private async void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TerminalViewModel model } && RequestCloseTabAsync is { } close)
            await close(model);
        e.Handled = true;
    }

    private async void OnWorkspaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.T)
        { RequestNewTab?.Invoke(); e.Handled = true; }
        else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.W
            && TabStrip.SelectedItem is TerminalViewModel selected && RequestCloseTabAsync is { } close)
        { e.Handled = true; await close(selected); }
        else if ((e.KeyModifiers == KeyModifiers.Control || e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
            && e.Key == Key.Tab && Tabs.Count > 0)
        {
            var direction = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
            TabStrip.SelectedIndex = (TabStrip.SelectedIndex + direction + Tabs.Count) % Tabs.Count;
            e.Handled = true;
        }
    }
}
