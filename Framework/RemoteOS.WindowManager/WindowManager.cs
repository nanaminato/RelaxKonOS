using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Input;
using RemoteOS.Core.Primitives;
using RemoteOS.Core.Windows;
using Rect = RemoteOS.Core.Primitives.Rect;
using Point = RemoteOS.Core.Primitives.Point;
using Size = RemoteOS.Core.Primitives.Size;
using WindowState = RemoteOS.Core.Windows.WindowState;

namespace RemoteOS.WindowManager;

/// <summary>
/// Authoritative desktop window manager. Windows are <see cref="RemoteWindow"/> controls parented
/// to a <see cref="Canvas"/> host; their position, size, z-order and state are owned here.
/// </summary>
public sealed class WindowManager : IWindowManager
{
    private readonly ObservableCollection<ManagedWindow> _windows = new();
    private readonly Dictionary<WindowId, WindowState> _preMinimizeState = new();
    private readonly Dictionary<WindowId, WindowState> _preFullScreenState = new();
    private readonly Dictionary<WindowId, Canvas> _windowHosts = new();
    private readonly List<IModalSession> _modalSessions = new();
    private readonly List<IShellModalSession> _shellModalSessions = new();

    private Canvas? _host;
    private Canvas? _fullScreenHost;
    private Rect _hostBounds;
    private Rect _fullScreenHostBounds;
    private int _zCounter;
    private int _nextId;
    private int _nextCascadeSlot;
    private ManagedWindow? _active;

    /// <summary>Set by the client shell when a connected workspace can persist window dimensions.</summary>
    public IWindowLayoutStore? LayoutStore { get; set; }

    public IReadOnlyList<ManagedWindow> Windows => _windows;
    public ManagedWindow? ActiveWindow => _active;
    public Rect HostBounds => _hostBounds;
    public Rect FullScreenHostBounds => _fullScreenHostBounds;

    public event EventHandler<ManagedWindow>? WindowOpened;
    public event EventHandler<ManagedWindow>? WindowClosed;
    public event EventHandler<ManagedWindow?>? ActiveWindowChanged;

    public void Attach(Canvas host)
    {
        _host = host;
        ReattachVisuals();
    }

    /// <summary>
    /// Removes only visual parents. Window metadata, RemoteWindow instances, modal chains and
    /// focus remain authoritative in this manager and are restored by the next Attach call.
    /// </summary>
    public void Detach()
    {
        foreach (var window in _windows)
            _windowHosts.TryGetValue(window.Info.Id, out var host);
        foreach (var host in _windowHosts.Values.Distinct().ToList())
            foreach (var window in _windows.Where(w => ReferenceEquals(GetWindowHost(w), host)))
                host.Children.Remove(window.View);
        foreach (var session in _modalSessions)
            session.Host.Children.Remove(session.Blocker);
        foreach (var session in _shellModalSessions)
            session.Host.Children.Remove(session.Blocker);
        _windowHosts.Clear();
        _host = null;
        _fullScreenHost = null;
    }

    public void AttachFullScreenHost(Canvas host)
    {
        _fullScreenHost = host;
        ReattachVisuals();
        UpdateFullScreenHostInteractivity();
    }

    public void SetHostBounds(Rect bounds)
    {
        _hostBounds = bounds;
        // Keep maximized windows filling the new area.
        foreach (var w in _windows)
        {
            if (w.Info.State == WindowState.Maximized)
            {
                w.Info.Bounds = bounds;
                w.View.ApplyBounds(bounds);
            }
        }

        foreach (var session in _modalSessions)
            session.Blocker.ApplyBounds(session.Owner.Info.Bounds);
        foreach (var session in _shellModalSessions.Where(session => !session.CoversFullDesktop))
            session.Blocker.ApplyBounds(_hostBounds);
    }

    public void SetFullScreenHostBounds(Rect bounds)
    {
        _fullScreenHostBounds = bounds;
        foreach (var window in _windows.Where(w => w.Info.State == WindowState.FullScreen))
        {
            window.Info.Bounds = bounds;
            window.View.ApplyBounds(bounds);
            UpdateDialogs(window);
        }
        foreach (var session in _shellModalSessions.Where(session => session.CoversFullDesktop))
            session.Blocker.ApplyBounds(bounds);
    }

    public ManagedWindow Create(WindowCreateOptions options)
    {
        if (_host == null)
            throw new InvalidOperationException("WindowManager is not attached to a host canvas.");

        var id = new WindowId(++_nextId);
        var layoutKey = GetLayoutKey(options.OwnerAppId, options.Title);
        var bounds = ResolveInitialBounds(options.Bounds, LayoutStore?.GetSize(layoutKey), options.InitialPlacement);

        var info = new WindowInfo
        {
            Id = id,
            OwnerAppId = options.OwnerAppId,
            Title = options.Title,
            IconGlyph = options.IconGlyph,
            IconPath = options.IconPath,
            Bounds = bounds,
            RestoreBounds = bounds,
            MinSize = new Size(240, 160),
            State = WindowState.Normal,
            CanResize = options.CanResize,
            CanMinimize = options.CanMinimize,
            CanMaximize = options.CanMaximize,
        };

        var view = new RemoteWindow { Content = options.Content };
        var managed = new ManagedWindow(info, view, options.IsModalDialog);
        view.DataContext = managed;

        managed.KeyDownFallback += (_, e) => HandleWindowKeyDown(managed, e);

        managed.FocusRequested += (_, _) => Focus(managed);
        managed.CloseRequested += (_, _) => Close(managed);
        managed.MinimizeRequested += (_, _) => Minimize(managed);
        managed.MaximizeToggleRequested += (_, _) => ToggleMaximize(managed);
        managed.TaskbarToggleRequested += (_, _) => ToggleTaskbar(managed);

        view.DragRequested += (_, e) => OnDrag(managed, e);
        view.ResizeRequested += (_, e) => OnResize(managed, e);
        view.FocusRequested += (_, _) => Focus(managed);

        _host.Children.Add(view);
        _windowHosts[id] = _host;
        view.ApplyBounds(info.Bounds);
        view.ApplyState(WindowState.Normal);

        _windows.Add(managed);
        WindowOpened?.Invoke(this, managed);

        Focus(managed);

        return managed;
    }

    public Task<TResult?> ShowDialogAsync<TResult>(
        ManagedWindow owner,
        string title,
        Func<ModalDialog<TResult>, Control> contentFactory,
        Rect? bounds = null)
    {
        if (_host == null)
            throw new InvalidOperationException("WindowManager is not attached to a host canvas.");
        if (!_windows.Contains(owner))
            throw new InvalidOperationException("The dialog owner is no longer open.");

        var dialog = new ModalDialog<TResult>(this, owner);
        var ownerBounds = owner.Info.Bounds;
        bounds ??= new Rect(
            ownerBounds.X + Math.Max(24, (ownerBounds.Width - 460) / 2),
            ownerBounds.Y + Math.Max(28, (ownerBounds.Height - 300) / 2),
            Math.Min(460, Math.Max(320, ownerBounds.Width - 48)),
            Math.Min(320, Math.Max(220, ownerBounds.Height - 56)));
        var dialogWindow = Create(new WindowCreateOptions(
            OwnerAppId: owner.Info.OwnerAppId,
            Title: title,
            Content: contentFactory(dialog),
            Bounds: bounds.Value,
            IconGlyph: owner.IconGlyph,
            CanResize: true,
            CanMinimize: false,
            CanMaximize: false,
            IsModalDialog: true,
            IconPath: owner.Info.IconPath));
        var dialogHost = GetWindowHost(owner);
        MoveToHost(dialogWindow, dialogHost);
        dialog.Attach(dialogWindow);

        var blocker = new ModalBlocker();
        blocker.ApplyBounds(owner.Info.Bounds);
        // Place the shield above only its owner and below the newly-created dialog window.
        blocker.ZIndex = dialogWindow.View.ZIndex - 1;
        // Clicking the shield over a blocked owner reactivates the modal chain: Focus walks
        // up to the root owner and down to the topmost modal dialog, which receives focus.
        blocker.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            Focus(owner);
        };
        dialogHost.Children.Add(blocker);

        var session = new ModalSession<TResult>(owner, dialogWindow, blocker, dialogHost, dialog);
        _modalSessions.Add(session);
        _ = dialog.Result.ContinueWith(_ => Dispatcher.UIThread.Post(() => CloseModalSession(session)),
            TaskScheduler.Default);
        return dialog.Result;
    }

    public Task<TResult?> ShowDialogAsync<TResult>(
        ManagedWindow owner,
        string title,
        Func<ModalDialog<TResult>, Control> contentFactory,
        Size preferredSize)
    {
        var ownerBounds = owner.Info.Bounds;
        var width = Math.Min(preferredSize.Width, Math.Max(320, ownerBounds.Width - 48));
        var height = Math.Min(preferredSize.Height, Math.Max(220, ownerBounds.Height - 56));
        var bounds = new Rect(
            ownerBounds.X + Math.Max(24, (ownerBounds.Width - width) / 2),
            ownerBounds.Y + Math.Max(28, (ownerBounds.Height - height) / 2),
            width,
            height);

        return ShowDialogAsync(owner, title, contentFactory, bounds);
    }

    public Task<TResult?> ShowShellDialogAsync<TResult>(
        string title,
        Func<ModalDialog<TResult>, Control> contentFactory,
        Size preferredSize)
    {
        if (_host == null)
            throw new InvalidOperationException("WindowManager is not attached to a host canvas.");

        var width = _hostBounds.Width > 0
            ? Math.Min(preferredSize.Width, Math.Max(320, _hostBounds.Width - 48))
            : preferredSize.Width;
        var height = _hostBounds.Height > 0
            ? Math.Min(preferredSize.Height, Math.Max(220, _hostBounds.Height - 56))
            : preferredSize.Height;
        var bounds = new Rect(
            _hostBounds.Width > 0 ? _hostBounds.X + Math.Max(24, (_hostBounds.Width - width) / 2) : 120,
            _hostBounds.Height > 0 ? _hostBounds.Y + Math.Max(28, (_hostBounds.Height - height) / 2) : 100,
            width,
            height);
        var dialog = new ModalDialog<TResult>(this, owner: null);
        var dialogWindow = Create(new WindowCreateOptions(
            OwnerAppId: new AppId("remoteos.shell"),
            Title: title,
            Content: contentFactory(dialog),
            Bounds: bounds,
            IconGlyph: "🖥",
            CanResize: true,
            CanMinimize: false,
            CanMaximize: false,
            IsModalDialog: true));
        dialog.Attach(dialogWindow);

        var blocker = new ModalBlocker();
        blocker.ApplyBounds(_hostBounds);
        blocker.ZIndex = dialogWindow.View.ZIndex - 1;
        blocker.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            Focus(dialogWindow);
        };
        _host.Children.Add(blocker);

        var session = new ShellModalSession<TResult>(dialogWindow, blocker, _host, dialog);
        _shellModalSessions.Add(session);
        _ = dialog.Result.ContinueWith(_ => Dispatcher.UIThread.Post(() => CloseShellModalSession(session)),
            TaskScheduler.Default);
        return dialog.Result;
    }

    public Task<TResult?> ShowSystemDialogAsync<TResult>(
        string title,
        Func<ModalDialog<TResult>, Control> contentFactory,
        Size preferredSize)
    {
        if (_host == null)
            throw new InvalidOperationException("WindowManager is not attached to a host canvas.");

        var bounds = GetFullScreenBounds();
        var width = Math.Min(preferredSize.Width, Math.Max(320, bounds.Width - 48));
        var height = Math.Min(preferredSize.Height, Math.Max(220, bounds.Height - 56));
        var dialogBounds = new Rect(
            bounds.X + Math.Max(24, (bounds.Width - width) / 2),
            bounds.Y + Math.Max(28, (bounds.Height - height) / 2),
            width,
            height);
        var dialog = new ModalDialog<TResult>(this, owner: null);
        var dialogWindow = Create(new WindowCreateOptions(
            OwnerAppId: new AppId("remoteos.shell"),
            Title: title,
            Content: contentFactory(dialog),
            Bounds: dialogBounds,
            IconGlyph: "🖥",
            CanResize: true,
            CanMinimize: false,
            CanMaximize: false,
            IsModalDialog: true));
        var dialogHost = GetFullScreenHost();
        MoveToHost(dialogWindow, dialogHost);
        dialog.Attach(dialogWindow);

        var blocker = new ModalBlocker();
        blocker.ApplyBounds(bounds);
        blocker.ZIndex = dialogWindow.View.ZIndex - 1;
        blocker.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            Focus(dialogWindow);
        };
        dialogHost.Children.Add(blocker);

        var session = new ShellModalSession<TResult>(dialogWindow, blocker, dialogHost, dialog, coversFullDesktop: true);
        _shellModalSessions.Add(session);
        UpdateFullScreenHostInteractivity();
        _ = dialog.Result.ContinueWith(_ => Dispatcher.UIThread.Post(() => CloseShellModalSession(session)),
            TaskScheduler.Default);
        return dialog.Result;
    }

    public void Close(ManagedWindow window)
    {
        if (!_windows.Remove(window))
            return;

        GetWindowHost(window).Children.Remove(window.View);

        _preMinimizeState.Remove(window.Info.Id);
        _preFullScreenState.Remove(window.Info.Id);
        _windowHosts.Remove(window.Info.Id);
        foreach (var session in _modalSessions.Where(s => ReferenceEquals(s.Owner, window) || ReferenceEquals(s.DialogWindow, window)).ToList())
            session.Cancel();
        foreach (var session in _shellModalSessions.Where(s => ReferenceEquals(s.DialogWindow, window)).ToList())
            session.Cancel();

        if (ReferenceEquals(_active, window))
        {
            _active = null;
            // Focus the topmost remaining visible window.
            var next = _windows.LastOrDefault(w => w.IsOnScreen) ?? _windows.LastOrDefault();
            if (next != null)
                Focus(next);
            else
                ActiveWindowChanged?.Invoke(this, null);
        }

        WindowClosed?.Invoke(this, window);
        UpdateFullScreenHostInteractivity();
    }

    private void CloseModalSession(IModalSession session)
    {
        if (!_modalSessions.Remove(session))
            return;

        session.Host.Children.Remove(session.Blocker);

        if (_windows.Contains(session.DialogWindow))
            Close(session.DialogWindow);
    }

    private void CloseShellModalSession(IShellModalSession session)
    {
        if (!_shellModalSessions.Remove(session))
            return;

        session.Host.Children.Remove(session.Blocker);

        if (_windows.Contains(session.DialogWindow))
            Close(session.DialogWindow);

        UpdateFullScreenHostInteractivity();
    }

    public void Focus(ManagedWindow window)
    {
        if (!_windows.Contains(window))
            return;

        // A shell modal has no individual application owner: it blocks the complete desktop
        // host. Keep it (and any nested child modal) at the activation target even if another
        // window tries to take focus while the shell flow is open.
        if (_shellModalSessions.LastOrDefault() is { } shellModal
            && _windows.Contains(shellModal.DialogWindow))
            window = shellModal.DialogWindow;

        // Activation always targets the topmost modal dialog of the clicked window's modal
        // chain. A modal owner stays blocked, so clicking it (or the input shield that covers
        // it) forwards activation to the dialog that currently owns it. The whole chain —
        // from the root owner down to the topmost modal — is raised together so each owner
        // sits just below its dialog and the entire group lands above every other window.
        // Non-modal child windows never create a session, so they form a chain of one and
        // activate directly (clicking a non-modal owner activates the owner, not its child).
        var chain = BuildModalChain(window);
        var target = chain[^1];

        foreach (var w in chain)
        {
            _zCounter++;
            w.View.ZIndex = _zCounter;
            // The shield that disables this owner sits just above it and just below its
            // modal dialog; raise it as part of the same group so it never lags behind.
            if (GetBlockerFor(w) is { } blocker)
            {
                _zCounter++;
                blocker.ZIndex = _zCounter;
            }
        }

        foreach (var w in _windows)
        {
            var active = ReferenceEquals(w, target);
            w.Info.IsFocused = active;
            w.IsActive = active;
            w.View.SetActive(active);
        }

        if (!ReferenceEquals(_active, target))
        {
            _active = target;
            ActiveWindowChanged?.Invoke(this, target);
        }

        target.View.Focus();
    }

    public void Minimize(ManagedWindow window)
    {
        if (window.Info.State == WindowState.Minimized)
            return;

        _preMinimizeState[window.Info.Id] = window.Info.State;
        CancelOwnedModalSessions(window);
        SetState(window, WindowState.Minimized);
        UpdateFullScreenHostInteractivity();

        if (ReferenceEquals(_active, window))
        {
            _active = null;
            var next = _windows.LastOrDefault(w => w.IsOnScreen && !ReferenceEquals(w, window));
            if (next != null)
                Focus(next);
            else
                ActiveWindowChanged?.Invoke(this, null);
        }
    }

    public void Restore(ManagedWindow window)
    {
        if (window.Info.State != WindowState.Minimized)
            return;

        _preMinimizeState.TryGetValue(window.Info.Id, out var previousState);
        _preMinimizeState.Remove(window.Info.Id);
        var restoredState = previousState is WindowState.Maximized or WindowState.FullScreen
            ? previousState
            : WindowState.Normal;
        SetState(window, restoredState);
        if (restoredState == WindowState.FullScreen)
        {
            MoveToHost(window, GetFullScreenHost());
            window.Info.Bounds = GetFullScreenBounds();
            window.View.ApplyBounds(window.Info.Bounds);
        }
        else if (restoredState == WindowState.Maximized)
        {
            MoveToHost(window, GetRegularHost());
            window.Info.Bounds = _hostBounds;
            window.View.ApplyBounds(_hostBounds);
        }
        else
        {
            MoveToHost(window, GetRegularHost());
            window.Info.Bounds = window.Info.RestoreBounds;
            window.View.ApplyBounds(window.Info.RestoreBounds);
        }
        Focus(window);
        UpdateFullScreenHostInteractivity();
    }

    /// <summary>Taskbar click: restore minimized windows, minimize the active one, otherwise focus.</summary>
    public void ToggleTaskbar(ManagedWindow window)
    {
        if (!_windows.Contains(window))
            return;
        if (window.Info.State == WindowState.Minimized)
            Restore(window);
        else if (ReferenceEquals(_active, window))
            Minimize(window);
        else
            Focus(window);
    }

    public void ToggleMaximize(ManagedWindow window)
    {
        switch (window.Info.State)
        {
            case WindowState.Maximized:
                SetState(window, WindowState.Normal);
                window.Info.Bounds = window.Info.RestoreBounds;
                window.View.ApplyBounds(window.Info.RestoreBounds);
                UpdateDialogs(window);
                break;
            case WindowState.Minimized:
                // Restore straight to maximized.
                SetState(window, WindowState.Maximized);
                window.Info.Bounds = _hostBounds;
                window.View.ApplyBounds(_hostBounds);
                UpdateDialogs(window);
                Focus(window);
                break;
            case WindowState.FullScreen:
                ExitFullScreen(window);
                break;
            default:
                window.Info.RestoreBounds = window.Info.Bounds;
                SetState(window, WindowState.Maximized);
                window.Info.Bounds = _hostBounds;
                window.View.ApplyBounds(_hostBounds);
                UpdateDialogs(window);
                break;
        }
    }

    /// <summary>Moves a managed window into the shell-wide full-screen overlay.</summary>
    public void EnterFullScreen(ManagedWindow window)
    {
        if (!_windows.Contains(window) || window.Info.State == WindowState.FullScreen)
            return;

        if (window.Info.State == WindowState.Minimized)
        {
            Restore(window);
            if (window.Info.State == WindowState.FullScreen)
                return;
        }

        CancelOwnedModalSessions(window);
        _preFullScreenState[window.Info.Id] = window.Info.State;
        MoveToHost(window, GetFullScreenHost());
        SetState(window, WindowState.FullScreen);
        window.Info.Bounds = GetFullScreenBounds();
        window.View.ApplyBounds(window.Info.Bounds);
        Focus(window);
        UpdateFullScreenHostInteractivity();
    }

    /// <summary>Returns a full-screen managed window to the state it had before entering full screen.</summary>
    public void ExitFullScreen(ManagedWindow window)
    {
        if (!_windows.Contains(window) || window.Info.State != WindowState.FullScreen)
            return;

        CancelOwnedModalSessions(window);
        _preFullScreenState.TryGetValue(window.Info.Id, out var previousState);
        _preFullScreenState.Remove(window.Info.Id);
        var restoredState = previousState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        MoveToHost(window, GetRegularHost());
        SetState(window, restoredState);
        if (restoredState == WindowState.Maximized)
        {
            window.Info.Bounds = _hostBounds;
            window.View.ApplyBounds(_hostBounds);
        }
        else
        {
            window.Info.Bounds = window.Info.RestoreBounds;
            window.View.ApplyBounds(window.Info.RestoreBounds);
        }
        UpdateDialogs(window);
        Focus(window);
        UpdateFullScreenHostInteractivity();
    }

    private void SetState(ManagedWindow window, WindowState state)
    {
        window.Info.State = state;
        window.Sync();
        window.View.ApplyState(state);
    }

    private void OnDrag(ManagedWindow window, DragBoundsEventArgs e)
    {
        if (window.Info.State != WindowState.Normal)
            return;

        var moved = e.StartBounds.WithPosition(e.StartBounds.Position + e.Delta);
        moved = ClampDrag(moved);
        window.Info.Bounds = moved;
        window.View.ApplyBounds(moved);
        UpdateDialogs(window);
    }

    private void OnResize(ManagedWindow window, ResizeBoundsEventArgs e)
    {
        if (window.Info.State != WindowState.Normal)
            return;

        var resized = ComputeResize(e.StartBounds, e.Edge, e.Delta, window.Info.MinSize, _hostBounds);
        window.Info.Bounds = resized;
        window.Info.RestoreBounds = resized;
        window.View.ApplyBounds(resized);
        LayoutStore?.RecordSize(GetLayoutKey(window.Info.OwnerAppId, window.Info.Title), resized.Size);
        UpdateDialogs(window);
    }

    private void UpdateDialogs(ManagedWindow owner)
    {
        foreach (var session in _modalSessions.Where(s => ReferenceEquals(s.Owner, owner)))
            session.Blocker.ApplyBounds(owner.Info.Bounds);
    }

    private void CancelOwnedModalSessions(ManagedWindow owner)
    {
        foreach (var session in _modalSessions.Where(s => ReferenceEquals(s.Owner, owner)).ToList())
            session.Cancel();
    }

    private void HandleWindowKeyDown(ManagedWindow window, RemoteKeyEventArgs e)
    {
        // Keep the conventional desktop close gesture available to every managed application.
        // Dialogs retain their existing Escape-to-cancel behavior below; Alt+F4 closes the
        // focused managed surface in the same way as its title-bar close button.
        if (!e.IsRepeat
            && e.Key == new RemoteKey("F4")
            && e.Modifiers == RemoteKeyModifiers.Alt)
        {
            Close(window);
            e.Handled = true;
            return;
        }

        if (e.Key != RemoteKey.Escape)
            return;

        // A modal is always the active window for its owner. Esc first cancels that modal,
        // before any full-screen or shell fallback can observe the key.
        if (window.IsModalDialog)
        {
            var session = _modalSessions.LastOrDefault(candidate => ReferenceEquals(candidate.DialogWindow, window));
            if (session is not null)
            {
                session.Cancel();
                e.Handled = true;
                return;
            }

            var shellSession = _shellModalSessions.LastOrDefault(candidate => ReferenceEquals(candidate.DialogWindow, window));
            if (shellSession is not null)
            {
                shellSession.Cancel();
                e.Handled = true;
            }
            return;
        }

        if (window.Info.State == WindowState.FullScreen)
        {
            ExitFullScreen(window);
            e.Handled = true;
        }
    }

    private ManagedWindow? GetTopmostModal(ManagedWindow owner)
        => _modalSessions.LastOrDefault(session => ReferenceEquals(session.Owner, owner))?.DialogWindow;

    /// <summary>The window that opened <paramref name="dialog"/> as its modal dialog, if any.</summary>
    private ManagedWindow? GetModalOwner(ManagedWindow dialog)
        => _modalSessions.FirstOrDefault(session => ReferenceEquals(session.DialogWindow, dialog))?.Owner;

    /// <summary>The shield that disables <paramref name="owner"/>, if it currently owns a modal dialog.</summary>
    private ModalBlocker? GetBlockerFor(ManagedWindow owner)
        => _modalSessions.LastOrDefault(session => ReferenceEquals(session.Owner, owner))?.Blocker;

    /// <summary>
    /// The full activation chain for a clicked window: from the root owner (the window no
    /// modal session claims as its dialog) down to the topmost modal dialog. Clicking any
    /// window in a modal chain reactivates the whole chain with the leaf dialog on top; a
    /// non-modal window forms a chain of one and activates directly.
    /// </summary>
    private List<ManagedWindow> BuildModalChain(ManagedWindow window)
    {
        // Walk up through modal owners to the root of the chain.
        var ancestors = new Stack<ManagedWindow>();
        var current = window;
        while (current is not null)
        {
            ancestors.Push(current);
            current = GetModalOwner(current);
        }

        var chain = new List<ManagedWindow>(ancestors); // root first, clicked window last so far
        // Extend from the clicked window down to the topmost modal it (transitively) owns.
        var node = window;
        while (GetTopmostModal(node) is { } dialog)
        {
            chain.Add(dialog);
            node = dialog;
        }
        return chain;
    }

    private Canvas GetRegularHost()
        => _host ?? throw new InvalidOperationException("WindowManager is not attached to a host canvas.");

    private Canvas GetFullScreenHost() => _fullScreenHost ?? GetRegularHost();

    private Rect GetFullScreenBounds() => _fullScreenHostBounds.IsEmpty ? _hostBounds : _fullScreenHostBounds;

    private Canvas GetWindowHost(ManagedWindow window)
        => _windowHosts.TryGetValue(window.Info.Id, out var host) ? host : GetRegularHost();

    private void MoveToHost(ManagedWindow window, Canvas destination)
    {
        var source = GetWindowHost(window);
        if (ReferenceEquals(source, destination))
            return;

        source.Children.Remove(window.View);
        destination.Children.Add(window.View);
        _windowHosts[window.Info.Id] = destination;
    }

    private void ReattachVisuals()
    {
        if (_host is null) return;
        foreach (var window in _windows)
        {
            var destination = window.Info.State == WindowState.FullScreen ? GetFullScreenHost() : _host;
            if (!destination.Children.Contains(window.View)) destination.Children.Add(window.View);
            _windowHosts[window.Info.Id] = destination;
        }
        foreach (var session in _modalSessions)
        {
            var host = GetWindowHost(session.Owner);
            session.Rehost(host);
            if (!host.Children.Contains(session.Blocker)) host.Children.Add(session.Blocker);
        }
        foreach (var session in _shellModalSessions)
        {
            var host = session.CoversFullDesktop ? GetFullScreenHost() : _host;
            session.Rehost(host);
            if (!host.Children.Contains(session.Blocker)) host.Children.Add(session.Blocker);
        }
    }

    private void UpdateFullScreenHostInteractivity()
    {
        if (_fullScreenHost is not null)
            _fullScreenHost.IsHitTestVisible = _windows.Any(w => w.Info.State == WindowState.FullScreen)
                || _shellModalSessions.Any(session => session.CoversFullDesktop);
    }

    private Rect ResolveInitialBounds(
        Rect? requested,
        Size? rememberedSize,
        WindowInitialPlacement initialPlacement)
    {
        var host = _hostBounds;
        if (host.IsEmpty)
            host = new Rect(0, 0, 1280, 720);

        var requestedWidth = requested?.Width ?? Math.Min(900, host.Width - 80);
        var requestedHeight = requested?.Height ?? Math.Min(560, host.Height - 80);
        double w = Math.Min(rememberedSize?.Width ?? requestedWidth, host.Width);
        double h = Math.Min(rememberedSize?.Height ?? requestedHeight, host.Height);
        var useDefaultPlacement = requested is null || initialPlacement == WindowInitialPlacement.CenteredCascade;
        var cascade = useDefaultPlacement ? (_nextCascadeSlot++ % 6) * 28 : 0;
        var explicitPosition = requested?.Position;

        double x = !useDefaultPlacement && explicitPosition.HasValue
            ? explicitPosition.Value.X
            : host.X + Math.Max(0, (host.Width - w) / 2) + cascade;
        double y = !useDefaultPlacement && explicitPosition.HasValue
            ? explicitPosition.Value.Y
            : host.Y + Math.Max(0, (host.Height - h) / 2) + cascade;

        x = Math.Clamp(x, host.X, Math.Max(host.X, host.Right - w));
        y = Math.Clamp(y, host.Y, Math.Max(host.Y, host.Bottom - h));
        return new Rect(x, y, w, h);
    }

    private static string GetLayoutKey(RemoteOS.Core.Applications.AppId appId, string title)
        => $"{appId.Value}:{title}";

    private Rect ClampDrag(Rect b)
    {
        var host = _hostBounds;
        var maxLeft = host.Right - 120;
        var minLeft = host.X - b.Width + 120;
        var x = Math.Clamp(b.X, minLeft, maxLeft);
        var y = Math.Clamp(b.Y, host.Y, Math.Max(host.Y, host.Bottom - 36));
        return new Rect(x, y, b.Width, b.Height);
    }

    private static Rect ComputeResize(Rect s, ResizeEdge edge, Point d, Size min, Rect host)
    {
        double x = s.X, y = s.Y, w = s.Width, h = s.Height;

        if (edge.HasFlag(ResizeEdge.Left))
        {
            var nx = s.X + d.X;
            if (s.Right - nx < min.Width)
                nx = s.Right - min.Width;
            nx = Math.Max(nx, host.X);
            x = nx;
            w = s.Right - nx;
        }

        if (edge.HasFlag(ResizeEdge.Right))
        {
            var nw = Math.Min(s.Width + d.X, host.Right - s.X);
            w = Math.Max(nw, min.Width);
        }

        if (edge.HasFlag(ResizeEdge.Top))
        {
            var ny = s.Y + d.Y;
            if (s.Bottom - ny < min.Height)
                ny = s.Bottom - min.Height;
            ny = Math.Max(ny, host.Y);
            y = ny;
            h = s.Bottom - ny;
        }

        if (edge.HasFlag(ResizeEdge.Bottom))
        {
            var nh = Math.Min(s.Height + d.Y, host.Bottom - s.Y);
            h = Math.Max(nh, min.Height);
        }

        return new Rect(x, y, w, h);
    }
}
