using Avalonia.Controls;
using Avalonia.Media;
using RelaxKonOS.Core.Primitives;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.WindowManager;

/// <summary>
/// Handle for a modal desktop window. A dialog is a real managed window: it can be moved and
/// resized like any other program window, while its direct owner (or the desktop host for a
/// shell modal) is blocked.
/// </summary>
public sealed class ModalDialog<TResult>
{
    private readonly WindowManager _manager;
    private readonly TaskCompletionSource<TResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ManagedWindow? _window;

    internal ModalDialog(WindowManager manager, ManagedWindow? owner)
    {
        _manager = manager;
        Owner = owner;
    }

    internal ManagedWindow? Owner { get; }
    public ManagedWindow? Window => _window;
    public Task<TResult?> Result => _completion.Task;

    public void Close(TResult result) => _completion.TrySetResult(result);
    public void Cancel() => _completion.TrySetResult(default);

    /// <summary>Opens a child modal window whose owner is this dialog window.</summary>
    public Task<TChild?> ShowDialogAsync<TChild>(
        string title,
        Func<ModalDialog<TChild>, Control> contentFactory)
    {
        if (_window is null)
            throw new InvalidOperationException("The dialog has not been shown yet.");
        return _manager.ShowDialogAsync(_window, title, contentFactory);
    }

    /// <summary>Opens a sized child modal window whose owner is this dialog window.</summary>
    public Task<TChild?> ShowDialogAsync<TChild>(
        string title,
        Func<ModalDialog<TChild>, Control> contentFactory,
        Size preferredSize)
    {
        if (_window is null)
            throw new InvalidOperationException("The dialog has not been shown yet.");
        return _manager.ShowDialogAsync(_window, title, contentFactory, preferredSize);
    }

    internal void Attach(ManagedWindow window) => _window = window;
}

internal interface IModalSession
{
    ManagedWindow Owner { get; }
    ManagedWindow DialogWindow { get; }
    ModalBlocker Blocker { get; }
    Canvas Host { get; }
    void Rehost(Canvas host);
    void Cancel();
}

internal interface IShellModalSession
{
    ManagedWindow DialogWindow { get; }
    ModalBlocker Blocker { get; }
    Canvas Host { get; }
    void Rehost(Canvas host);
    bool CoversFullDesktop { get; }
    void Cancel();
}

internal sealed class ModalSession<TResult>(
    ManagedWindow owner,
    ManagedWindow dialogWindow,
    ModalBlocker blocker,
    Canvas host,
    ModalDialog<TResult> dialog) : IModalSession
{
    public ManagedWindow Owner { get; } = owner;
    public ManagedWindow DialogWindow { get; } = dialogWindow;
    public ModalBlocker Blocker { get; } = blocker;
    public Canvas Host { get; private set; } = host;
    public void Rehost(Canvas host) => Host = host;
    public void Cancel() => dialog.Cancel();
}

internal sealed class ShellModalSession<TResult>(
    ManagedWindow dialogWindow,
    ModalBlocker blocker,
    Canvas host,
    ModalDialog<TResult> dialog,
    bool coversFullDesktop = false) : IShellModalSession
{
    public ManagedWindow DialogWindow { get; } = dialogWindow;
    public ModalBlocker Blocker { get; } = blocker;
    public Canvas Host { get; private set; } = host;
    public void Rehost(Canvas host) => Host = host;
    public bool CoversFullDesktop { get; } = coversFullDesktop;
    public void Cancel() => dialog.Cancel();
}

/// <summary>A transparent input shield over a blocked owner window or the desktop host.</summary>
internal sealed class ModalBlocker : Border
{
    public ModalBlocker()
    {
        // The brush lives in Application.Styles, so resolving it once through
        // Application.Resources falls back to transparent. A dynamic resource binding also
        // keeps an already-open modal in sync when the workspace theme changes.
        Bind(BackgroundProperty, this.GetResourceObservable("DialogScrimBrush"));
    }

    public void ApplyBounds(Rect bounds)
    {
        Canvas.SetLeft(this, bounds.X);
        Canvas.SetTop(this, bounds.Y);
        Width = bounds.Width;
        Height = bounds.Height;
    }
}
