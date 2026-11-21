using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Services;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>
/// Holds the control's VT lifetime stable while an asynchronous transport starts.
/// RoyalTerminal 0.6.3 binds its input mode source before awaiting transport startup,
/// but reports HasActiveTransport only afterward. Idle layout/history updates can
/// otherwise dispose the processor still referenced by that input mode source.
/// </summary>
internal sealed class ConnectingTerminalSessionService : ITerminalSessionService, ITerminalOutputLeaseSource
{
    private readonly RoyalTerminal.Terminal.Services.TerminalSessionService _inner = new();
    private int _starting;

    public bool HasActiveTransport => Volatile.Read(ref _starting) != 0 || _inner.HasActiveTransport;
    public ITerminalEndpoint? Endpoint => _inner.Endpoint;
    public ITerminalInputSink? InputSink => _inner.InputSink;
    public ITerminalSelectionSource? SelectionSource => _inner.SelectionSource;
    public ITerminalModeSource? ModeSource => _inner.ModeSource;
    public ITerminalTransport? Transport => _inner.Transport;
    public IPty? Pty => _inner.Pty;
    public bool HasPty => _inner.HasPty;
    public Action<TerminalOutputLease>? OutputLeaseCallback
    {
        get => _inner.OutputLeaseCallback;
        set => _inner.OutputLeaseCallback = value;
    }
    public event EventHandler<TerminalSessionInputEventArgs>? InputSent
    {
        add => _inner.InputSent += value;
        remove => _inner.InputSent -= value;
    }

    public void AttachEndpoint(ITerminalEndpoint endpoint) => _inner.AttachEndpoint(endpoint);
    public void DetachEndpoint() => _inner.DetachEndpoint();
    public void SendInput(string text) => _inner.SendInput(text);
    public void SendInput(ReadOnlySpan<byte> data) => _inner.SendInput(data);

    public async ValueTask StartSessionAsync(
        ITerminalTransportFactory transportFactory, ITerminalTransportOptions transportOptions,
        IVtProcessor? vtProcessor, Action<byte[], int> onTransportDataReceived,
        Action<int> onTransportProcessExited, Action<byte[]> onVtResponse,
        Action onVtBell, Action<string> onVtTitleChanged, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0)
            throw new InvalidOperationException("A terminal transport is already starting.");
        try
        {
            await _inner.StartSessionAsync(transportFactory, transportOptions, vtProcessor,
                onTransportDataReceived, onTransportProcessExited, onVtResponse,
                onVtBell, onVtTitleChanged, cancellationToken).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref _starting, 0); }
    }

    public void StartPty(IPtyFactory ptyFactory, string? shell, int columns, int rows,
        string? workingDirectory, IVtProcessor? vtProcessor, Action<byte[], int> onPtyDataReceived,
        Action<int> onPtyProcessExited, Action<byte[]> onVtResponse, Action onVtBell,
        Action<string> onVtTitleChanged, IReadOnlyList<string>? arguments = null)
    {
        if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0)
            throw new InvalidOperationException("A terminal transport is already starting.");
        try
        {
            _inner.StartPty(ptyFactory, shell, columns, rows, workingDirectory, vtProcessor,
                onPtyDataReceived, onPtyProcessExited, onVtResponse, onVtBell, onVtTitleChanged, arguments);
        }
        finally { Volatile.Write(ref _starting, 0); }
    }

    public void StopPty(IVtProcessor? vtProcessor, Action<byte[], int> onPtyDataReceived,
        Action<int> onPtyProcessExited) => _inner.StopPty(vtProcessor, onPtyDataReceived, onPtyProcessExited);
    public ValueTask StopSessionAsync(IVtProcessor? vtProcessor, Action<byte[], int> onTransportDataReceived,
        Action<int> onTransportProcessExited) => _inner.StopSessionAsync(vtProcessor, onTransportDataReceived, onTransportProcessExited);
    public void ResizePty(int columns, int rows, int widthPixels, int heightPixels) =>
        _inner.ResizePty(columns, rows, widthPixels, heightPixels);
    public void ResizeSession(int columns, int rows, int widthPixels, int heightPixels) =>
        _inner.ResizeSession(columns, rows, widthPixels, heightPixels);
}
