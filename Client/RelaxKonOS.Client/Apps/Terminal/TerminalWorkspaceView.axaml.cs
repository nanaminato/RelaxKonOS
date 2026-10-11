using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>Retains each session view in the visual tree while switching the visible tab.</summary>
public partial class TerminalWorkspaceView : UserControl
{
    private readonly Dictionary<TerminalViewModel, TerminalView> _views = [];
    public ObservableCollection<TerminalViewModel> Tabs { get; } = [];
    public Action? RequestNewTab { get; set; }
    public Func<TerminalViewModel, Task>? RequestCloseTabAsync { get; set; }
    public TerminalWorkspaceView()
    {
        InitializeComponent();
        TabStrip.ItemsSource = Tabs;
        TabStrip.SelectionChanged += (_, _) => UpdateSelection();
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
        if (TabStrip.SelectedItem is TerminalViewModel selected && _views.TryGetValue(selected, out var active))
            active.FocusSession();
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
