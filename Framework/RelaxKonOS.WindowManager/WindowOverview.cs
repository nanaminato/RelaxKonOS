using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Core.Windows;

namespace RelaxKonOS.WindowManager;

/// <summary>
/// Read-only projection of one desktop window for the window overview / task switcher.
/// Deliberately carries no <see cref="ManagedWindow"/>: the overview is a host-owned system
/// surface and must not be able to mutate window state, reorder z-index or reach the application.
/// </summary>
/// <remarks>
/// The projection has no "can close" flag. Every window the overview lists is a regular, non-modal
/// window, and <see cref="IWindowManager.Close"/> always succeeds for one, so a flag would be a
/// constant. Modal dialogs are excluded outright, and they are the only windows whose lifetime is
/// owned by an in-flight flow rather than by the user.
/// </remarks>
public sealed record WindowOverviewItem(
    WindowId WindowId,
    AppId ApplicationId,
    string Title,
    string? IconGlyph,
    IImage? IconImage,
    bool IsActive,
    WindowState State,
    /// <summary>
    /// v1 always reports <c>false</c>: there is no safe, cheap way to capture a WebView or a
    /// native child window, so the overview renders an icon + title card instead. The flag exists
    /// so a later implementation can opt in per window without changing this contract.
    /// </summary>
    bool IsThumbnailAvailable)
{
    public bool HasIconImage => IconImage is not null;

    public bool HasIconGlyph => !string.IsNullOrEmpty(IconGlyph);

    public bool IsMinimized => State == WindowState.Minimized;

    public bool IsFullScreen => State == WindowState.FullScreen;

    public bool IsMaximized => State == WindowState.Maximized;
}

/// <summary>
/// Host-controlled command surface for the window overview. The shell and applications never own
/// window switching: they may only ask this controller, which is the only object allowed to change
/// which window is in front.
/// </summary>
public interface IWindowOverviewController
{
    bool IsOverviewVisible { get; }

    /// <summary>Overview items, most recently used first.</summary>
    IReadOnlyList<WindowOverviewItem> Items { get; }

    /// <summary>Index of the highlighted item, or -1 when the overview holds no windows.</summary>
    int SelectedIndex { get; }

    WindowId? SelectedWindowId { get; }

    /// <summary>Raised whenever <see cref="Items"/>, <see cref="SelectedIndex"/> or visibility changes.</summary>
    event EventHandler? Changed;

    /// <summary>Opens the overview. Returns false when there is nothing to show or it is already open.</summary>
    bool ShowOverview();

    void HideOverview();

    /// <summary>Opens the overview, or closes it when already open.</summary>
    bool ToggleOverview();

    /// <summary>Moves the highlight. <paramref name="direction"/> is -1 for previous, +1 for next.</summary>
    bool MoveSelection(int direction);

    /// <summary>Brings the highlighted window to the front, restores it, focuses it and closes the overview.</summary>
    bool ActivateSelection();

    /// <summary>Brings a specific window to the front, restoring it if minimized, and closes the overview.</summary>
    bool Activate(WindowId windowId);

    /// <summary>Closes the highlighted window; the overview stays open on the remaining windows.</summary>
    bool CloseSelection();

    /// <summary>Closes a specific window; the overview stays open on the remaining windows.</summary>
    bool Close(WindowId windowId);

    /// <summary>
    /// Alt-tab style stepping: jumps straight to the next window without showing the overview.
    /// Returns false when there is nothing to switch to.
    /// </summary>
    bool CycleApplication(int direction);
}

/// <summary>
/// Default projection over <see cref="IWindowManager"/>. It holds no window state of its own: every
/// item is rebuilt from the manager, so a window that closes while the overview is open simply
/// disappears instead of leaving a stale handle behind.
/// </summary>
public sealed class WindowOverviewController : IWindowOverviewController, IDisposable
{
    private readonly IWindowManager _manager;
    private readonly List<ManagedWindow> _observed = [];
    private readonly ObservableCollection<WindowOverviewItem> _items = [];

    private bool _isVisible;
    private int _selectedIndex = -1;
    private bool _disposed;

    public WindowOverviewController(IWindowManager manager)
    {
        _manager = manager;
        _manager.WindowOpened += OnWindowOpened;
        _manager.WindowClosed += OnWindowClosed;
        _manager.ActiveWindowChanged += OnActiveWindowChanged;
        Rebuild();
    }

    public bool IsOverviewVisible => _isVisible;

    public IReadOnlyList<WindowOverviewItem> Items => _items;

    public int SelectedIndex => _selectedIndex;

    public WindowId? SelectedWindowId =>
        _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex].WindowId : null;

    public event EventHandler? Changed;

    public bool ShowOverview()
    {
        Rebuild();
        if (_items.Count == 0 || _isVisible) return false;

        _isVisible = true;
        // Selection starts on the window after the active one, so a single confirm switches away
        // instead of being a no-op - the behaviour every desktop task switcher has.
        _selectedIndex = _items.Count > 1 ? 1 : 0;
        Raise();
        return true;
    }

    public void HideOverview()
    {
        if (!_isVisible) return;
        _isVisible = false;
        Raise();
    }

    public bool ToggleOverview()
    {
        if (_isVisible)
        {
            HideOverview();
            return false;
        }
        return ShowOverview();
    }

    public bool MoveSelection(int direction)
    {
        if (!_isVisible || _items.Count == 0 || direction == 0) return false;
        var step = direction > 0 ? 1 : -1;
        _selectedIndex = (_selectedIndex + step + _items.Count) % _items.Count;
        Raise();
        return true;
    }

    public bool ActivateSelection() => SelectedWindowId is { } id && Activate(id);

    public bool Activate(WindowId windowId)
    {
        var target = FindManaged(windowId);
        if (target is null) return false;

        HideOverview();
        // A minimized window is not on screen. Focusing it alone would leave an untouched window
        // in front, so the selection is restored first, exactly as clicking its taskbar entry does.
        if (target.Info.State == WindowState.Minimized) _manager.Restore(target);
        else _manager.Focus(target);
        return true;
    }

    public bool CloseSelection() => SelectedWindowId is { } id && Close(id);

    public bool Close(WindowId windowId)
    {
        var target = FindManaged(windowId);
        if (target is null) return false;

        _manager.Close(target);
        // Closing raises WindowClosed, which rebuilds the list and re-clamps the selection.
        if (_items.Count == 0) HideOverview();
        else Raise();
        return true;
    }

    public bool CycleApplication(int direction)
    {
        Rebuild();
        if (_items.Count < 2 || direction == 0) return false;

        var step = direction > 0 ? 1 : -1;
        var current = IndexOfActive();
        var start = current < 0 ? 0 : current;
        var next = (start + step + _items.Count) % _items.Count;
        var target = FindManaged(_items[next].WindowId);

        if (target is null) return false;
        if (target.Info.State == WindowState.Minimized) _manager.Restore(target);
        else _manager.Focus(target);
        return true;
    }

    private ManagedWindow? FindManaged(WindowId id) =>
        _manager.Windows.FirstOrDefault(window => window.Info.Id == id);

    private int IndexOfWindow(WindowId id)
    {
        for (var i = 0; i < _items.Count; i++)
            if (_items[i].WindowId == id) return i;
        return -1;
    }

    private int IndexOfActive()
    {
        for (var i = 0; i < _items.Count; i++)
            if (_items[i].IsActive) return i;
        return -1;
    }

    // ── Rebuild ──

    private void Rebuild()
    {
        var previous = SelectedWindowId;

        // Windows are kept bottom-to-top; the overview wants the active window first.
        var ordered = _manager.Windows
            .Where(window => !window.IsModalDialog)
            .Reverse()
            .ToList();

        _items.Clear();
        foreach (var window in ordered)
            _items.Add(Project(window));

        Observe(ordered);

        _selectedIndex = previous is null
            ? (_items.Count > 0 ? 0 : -1)
            : Math.Max(0, IndexOfWindow(previous.Value));

        if (_selectedIndex >= _items.Count) _selectedIndex = _items.Count - 1;
    }

    private static WindowOverviewItem Project(ManagedWindow window)
    {
        var info = window.Info;
        return new WindowOverviewItem(
            info.Id,
            info.OwnerAppId,
            info.Title,
            info.IconGlyph,
            window.IconImage,
            info.IsFocused,
            info.State,
            IsThumbnailAvailable: false);
    }

    /// <summary>
    /// Managed windows are observable, but the manager does not raise an event for a plain state
    /// change (minimize / maximize). Watching the projections keeps the overview accurate without
    /// adding a new event to the window manager's public surface.
    /// </summary>
    private void Observe(List<ManagedWindow> windows)
    {
        foreach (var window in _observed)
            window.PropertyChanged -= OnWindowPropertyChanged;
        _observed.Clear();

        foreach (var window in windows)
        {
            window.PropertyChanged += OnWindowPropertyChanged;
            _observed.Add(window);
        }
    }

    private void OnWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ManagedWindow window) return;
        // State and title are what the overview renders; icon changes arrive with the same sync.
        if (e.PropertyName is not (nameof(ManagedWindow.State) or nameof(ManagedWindow.Title) or nameof(ManagedWindow.IsActive)))
            return;

        var index = IndexOfWindow(window.Info.Id);
        if (index >= 0) _items[index] = Project(window);
    }

    private void OnWindowOpened(object? sender, ManagedWindow window)
    {
        Rebuild();
        Raise();
    }

    private void OnWindowClosed(object? sender, ManagedWindow window)
    {
        Rebuild();
        // An overview with nothing left to show would be an empty modal trap: close it instead.
        if (_items.Count == 0) HideOverview();
        else Raise();
    }

    private void OnActiveWindowChanged(object? sender, ManagedWindow? window)
    {
        // The active window moved, so every card's "is active" flag may have flipped. One pass
        // over a dictionary keeps this O(n) instead of scanning the window list per card.
        var byId = _manager.Windows.ToDictionary(w => w.Info.Id);
        for (var i = 0; i < _items.Count; i++)
        {
            if (!byId.TryGetValue(_items[i].WindowId, out var source)) continue;
            var updated = Project(source);
            if (updated != _items[i]) _items[i] = updated;
        }
        Raise();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _manager.WindowOpened -= OnWindowOpened;
        _manager.WindowClosed -= OnWindowClosed;
        _manager.ActiveWindowChanged -= OnActiveWindowChanged;
        Observe([]);
    }
}
