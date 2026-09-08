using Avalonia.Controls;
using RelaxKonOS.Core.Primitives;

namespace RelaxKonOS.WindowManager;

/// <summary>
/// The desktop window manager. Owns the lifecycle, z-ordering and interactive state of all
/// managed windows that live inside the shell's window host.
/// </summary>
public interface IWindowManager
{
    /// <summary>Read-only view of every window (including minimized ones), bottom-to-top.</summary>
    IReadOnlyList<ManagedWindow> Windows { get; }

    /// <summary>The currently focused window, or null.</summary>
    ManagedWindow? ActiveWindow { get; }

    /// <summary>Desktop area available to windows (excludes taskbar etc.).</summary>
    Rect HostBounds { get; }

    /// <summary>Binds the manager to the canvas that physically hosts window visuals.</summary>
    void Attach(Canvas host);

    /// <summary>Detaches visuals without closing windows or cancelling modal sessions.</summary>
    void Detach();

    /// <summary>
    /// Binds the optional shell-wide overlay used by full-screen windows. If no overlay is
    /// attached, full-screen windows fall back to the regular window host.
    /// </summary>
    void AttachFullScreenHost(Canvas host);

    /// <summary>Updates the available desktop area (e.g. on shell resize).</summary>
    void SetHostBounds(Rect bounds);

    /// <summary>Updates the area occupied by the shell-wide full-screen overlay.</summary>
    void SetFullScreenHostBounds(Rect bounds);

    /// <summary>Creates, lays out and shows a new window for the given application content.</summary>
    ManagedWindow Create(WindowCreateOptions options);

    /// <summary>Shows a modal dialog over an application window and asynchronously returns its result.</summary>
    Task<TResult?> ShowDialogAsync<TResult>(
        ManagedWindow owner,
        string title,
        Func<ModalDialog<TResult>, Control> contentFactory,
        Rect? bounds = null);

    /// <summary>Shows a modal dialog centered over its owner at the requested size.</summary>
    Task<TResult?> ShowDialogAsync<TResult>(
        ManagedWindow owner,
        string title,
        Func<ModalDialog<TResult>, Control> contentFactory,
        Size preferredSize);

    /// <summary>
    /// Shows a shell-owned modal dialog. Use this for desktop-level flows that do not have an
    /// application window owner; the desktop window host is blocked while the dialog is open.
    /// </summary>
    Task<TResult?> ShowShellDialogAsync<TResult>(
        string title,
        Func<ModalDialog<TResult>, Control> contentFactory,
        Size preferredSize);

    /// <summary>
    /// Shows a system-owned modal above every shell surface, including the taskbar. Use this
    /// only for security-sensitive flows that must prevent desktop interaction until resolved.
    /// </summary>
    Task<TResult?> ShowSystemDialogAsync<TResult>(
        string title,
        Func<ModalDialog<TResult>, Control> contentFactory,
        Size preferredSize);

    void Close(ManagedWindow window);
    void Focus(ManagedWindow window);
    void Minimize(ManagedWindow window);
    void Restore(ManagedWindow window);
    void ToggleMaximize(ManagedWindow window);
    void EnterFullScreen(ManagedWindow window);
    void ExitFullScreen(ManagedWindow window);

    event EventHandler<ManagedWindow>? WindowOpened;
    event EventHandler<ManagedWindow>? WindowClosed;
    event EventHandler<ManagedWindow?>? ActiveWindowChanged;
}
