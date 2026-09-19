using System.Collections.ObjectModel;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Core.Windows;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Services.SystemUi;

/// <summary>
/// Presentation state for one card in the window overview. It exists because selection is
/// cross-item state: an <see cref="ItemsControl"/> can style a highlighted row on its own, but a
/// wrapping card grid cannot, so the selected card is marked here instead of via a list control
/// whose built-in keyboard navigation would fight the host's global shortcuts.
/// </summary>
public sealed partial class WindowOverviewCard : ObservableObject
{
    public WindowOverviewCard(WindowOverviewItem item) => Item = item;

    [ObservableProperty] private WindowOverviewItem _item;

    [ObservableProperty] private bool _isSelected;

    public WindowId WindowId => Item.WindowId;
}

/// <summary>
/// Host-owned system UI layer. It is the single place that decides whether the window overview is
/// open and the only place that turns global keyboard shortcuts into overview commands, so neither
/// a shell package nor an application can capture or re-route them.
/// </summary>
/// <remarks>
/// The coordinator deliberately exposes observable state: the overview view binds to it directly,
/// which keeps window-switch truth (<see cref="IWindowOverviewController"/>) in the framework and
/// presentation in one host object rather than in a second, redundant view model.
/// </remarks>
public sealed class SystemUiCoordinator : ObservableObject, IDisposable
{
    private readonly IWindowManager _windows;
    private readonly IWindowOverviewController _overview;
    private readonly ObservableCollection<WindowOverviewCard> _cards = [];

    private WindowId? _focusReturn;
    private bool _disposed;

    public SystemUiCoordinator(IWindowManager windows, IWindowOverviewController overview)
    {
        _windows = windows;
        _overview = overview;
        _overview.Changed += OnOverviewChanged;
    }

    /// <summary>Overview cards in display order, most recently used first.</summary>
    public IReadOnlyList<WindowOverviewCard> Cards => _cards;

    public bool IsOverviewVisible => _overview.IsOverviewVisible;

    public int SelectedIndex => _overview.SelectedIndex;

    public bool HasWindows => _cards.Count > 0;

    /// <summary>Raised after any state the host window mirrors (visibility, selection, cards).</summary>
    public event EventHandler? Changed;

    // ── Commands ──

    /// <summary>
    /// Opens the overview unless the desktop is blocked by a shell/system modal, which outranks the
    /// task switcher, or unless there is nothing to switch between.
    /// </summary>
    public bool ShowOverview()
    {
        if (_windows.IsSystemModalOpen) return false;
        if (_overview.IsOverviewVisible) return true;

        // Remember what had focus so closing the overview without picking another window puts the
        // user back where they were, rather than on an arbitrary window.
        _focusReturn = _windows.ActiveWindow?.Info.Id;
        if (!_overview.ShowOverview())
        {
            _focusReturn = null;
            return false;
        }
        return true;
    }

    /// <summary>Closes the overview and restores the focus it borrowed.</summary>
    public void HideOverview()
    {
        if (!_overview.IsOverviewVisible) return;

        var returnTo = _focusReturn;
        _focusReturn = null;
        _overview.HideOverview();
        RestoreFocus(returnTo);
    }

    public bool ToggleOverview()
    {
        if (_overview.IsOverviewVisible)
        {
            HideOverview();
            return false;
        }
        return ShowOverview();
    }

    public bool MoveSelection(int direction) => _overview.MoveSelection(direction);

    /// <summary>Brings the highlighted window forward. The target receives focus, so nothing is restored.</summary>
    public bool ActivateSelection()
    {
        _focusReturn = null;
        return _overview.ActivateSelection();
    }

    /// <summary>Brings a specific window forward, used when a card is clicked.</summary>
    public bool ActivateWindow(WindowId windowId)
    {
        // The target receives focus, so nothing needs to be restored afterwards.
        _focusReturn = null;
        return _overview.Activate(windowId);
    }

    public bool CloseSelection() => _overview.CloseSelection();

    /// <summary>Closes a specific window from its card; the overview stays open on the rest.</summary>
    public bool CloseWindow(WindowId windowId) => _overview.Close(windowId);

    public bool CycleApplication(int direction)
    {
        if (_windows.IsSystemModalOpen) return false;
        return _overview.CycleApplication(direction);
    }

    /// <summary>
    /// Global shortcut routing. Returns true when the coordinator consumed the key, which is what
    /// lets the host stop a managed window from also acting on it.
    /// </summary>
    public bool HandleKey(Key key, KeyModifiers modifiers)
    {
        if (_overview.IsOverviewVisible)
        {
            switch (key)
            {
                case Key.Escape:
                    HideOverview();
                    return true;
                case Key.Left:
                case Key.Up:
                    MoveSelection(-1);
                    return true;
                case Key.Right:
                case Key.Down:
                case Key.Tab:
                    MoveSelection(1);
                    return true;
                case Key.Enter:
                case Key.Space:
                    ActivateSelection();
                    return true;
                case Key.Delete:
                    CloseSelection();
                    return true;
                default:
                    return false;
            }
        }

        if (key != Key.Tab) return false;

        // Win+Tab is the task-view gesture; Alt+Tab steps straight through windows the way the
        // quick switch does on every desktop, and Shift reverses it.
        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            // A second Win+Tab must not close the view that the first one opened, so this gesture
            // only opens. When a shell/system modal owns the desktop the key is still consumed, so
            // Tab cannot reach inside the prompt and move focus there.
            if (_overview.IsOverviewVisible) return true;
            ShowOverview();
            return true;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
            return CycleApplication(modifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        return false;
    }

    private void RestoreFocus(WindowId? windowId)
    {
        if (windowId is not { } id) return;
        var window = _windows.Windows.FirstOrDefault(candidate => candidate.Info.Id == id);
        if (window is not null) _windows.Focus(window);
    }

    // ── Projection ──

    private void OnOverviewChanged(object? sender, EventArgs e) => Sync();

    /// <summary>
    /// Reconciles the card list with the projection. Cards are matched by window id so an existing
    /// card keeps its instance: a re-used card does not restart its theme transition, and the panel
    /// does not flicker every time the active window changes.
    /// </summary>
    private void Sync()
    {
        var items = _overview.Items;
        var existing = _cards.ToDictionary(card => card.WindowId);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var isSelected = i == _overview.SelectedIndex;

            if (existing.TryGetValue(item.WindowId, out var card))
            {
                card.Item = item;
                card.IsSelected = isSelected;
                var current = _cards.IndexOf(card);
                // Positions before i already hold the right cards, so moving a later card up cannot
                // disturb them.
                if (current != i) _cards.Move(current, i);
            }
            else
            {
                card = new WindowOverviewCard(item) { IsSelected = isSelected };
                _cards.Insert(Math.Min(i, _cards.Count), card);
            }
        }

        while (_cards.Count > items.Count) _cards.RemoveAt(_cards.Count - 1);

        // The controller also hides itself when the last window closes; drop the stored focus so a
        // later, unrelated hide cannot resurrect a window that no longer exists.
        if (!_overview.IsOverviewVisible) _focusReturn = null;

        OnPropertyChanged(nameof(IsOverviewVisible));
        OnPropertyChanged(nameof(SelectedIndex));
        OnPropertyChanged(nameof(HasWindows));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _overview.Changed -= OnOverviewChanged;
    }
}
