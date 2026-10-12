using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Workspace;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>Thrown when the WebView never produced a usable terminal page on this host.</summary>
public sealed class TerminalSurfaceUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// A terminal rendered by xterm.js inside the platform WebView.
///
/// The page is a renderer only: this surface owns the same <see cref="ITerminalTransport"/> the
/// in-process control would use (server PTY over SignalR, SSH or a local PTY), pushes the session's
/// bytes into the page and forwards the user's intent back. Nothing about reaching the session
/// changes because the engine changed — which is what makes the engine a per-workspace preference
/// rather than a per-transport one.
/// </summary>
public sealed class XtermJsTerminalSurface : ITerminalSurface
{
    private const int ScrollbackLimit = 10000;
    private const double MinimumFontSize = 8;
    private const double MaximumFontSize = 40;
    /// <summary>How long the page may take to announce itself before the window falls back.</summary>
    private static readonly TimeSpan PageReadyTimeout = TimeSpan.FromSeconds(20);
    /// <summary>Bytes handed to one script invocation. Keeps a large burst from building one huge string.</summary>
    private const int MaximumFlushBytes = 96 * 1024;
    /// <summary>Output held while the page is still starting, so a chatty shell cannot grow without bound.</summary>
    private const int MaximumPendingBytes = 4 * 1024 * 1024;

    private readonly ITerminalTransportFactory _factory;
    private readonly NativeWebView _view = new();
    private readonly TaskCompletionSource<bool> _pageReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _pump;
    private readonly object _bufferGate = new();
    private readonly List<byte> _pending = [];

    private ITerminalTransport? _transport;
    private IReadOnlyDictionary<string, string> _labels;
    private TerminalSettingsDto _appearance = TerminalSettingsDto.Default;
    private TerminalSessionDimensions? _requestedDimensions;
    private string _selection = string.Empty;
    private bool _navigated;
    private bool _pumping;
    private bool _disposed;
    private int _wakeQueued;

    public XtermJsTerminalSurface(ITerminalTransportFactory factory, IReadOnlyDictionary<string, string> labels)
    {
        _factory = factory;
        _labels = labels;
        _view.WebMessageReceived += OnWebMessageReceived;
        _view.Loaded += OnViewLoaded;
        _pump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _pump.Tick += (_, _) => FlushPending();
        ApplySurfaceBackground();
    }

    public Control View => _view;

    public bool HasSelection => !string.IsNullOrEmpty(_selection);

    public event EventHandler<int>? ProcessExited;
    public event EventHandler<string>? TitleChanged;
    public event EventHandler? SelectionChanged;

    public async Task StartSessionAsync(ITerminalTransportOptions options, CancellationToken cancellationToken)
    {
        // The page has to exist before a PTY is attached: an unusable WebView is discovered here,
        // while the caller can still open the same session with the in-process control instead.
        await WaitForPageAsync(cancellationToken).ConfigureAwait(true);

        var transport = _factory.Create(options);
        _transport = transport;
        transport.DataReceived += OnTransportData;
        transport.ProcessExited += OnTransportExited;
        try
        {
            await transport.StartAsync(options, cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            transport.DataReceived -= OnTransportData;
            transport.ProcessExited -= OnTransportExited;
            _transport = null;
            transport.Dispose();
            throw;
        }

        // The page fits itself to the window before a PTY exists, so its first measurement is replayed.
        if (_requestedDimensions is { } dimensions)
            transport.Resize(dimensions);
    }

    public void StopSession()
    {
        var transport = _transport;
        _transport = null;
        if (transport is null)
            return;

        transport.DataReceived -= OnTransportData;
        transport.ProcessExited -= OnTransportExited;
        _ = StopAsync(transport);
    }

    public void Focus()
    {
        _view.Focus();
        Invoke("window.rkTerminal.focus()");
    }

    public async Task CopySelectionAsync()
    {
        var text = _selection;
        if (string.IsNullOrEmpty(text))
            return;
        try
        {
            if (TopLevel.GetTopLevel(_view)?.Clipboard is { } clipboard)
                await SetClipboardTextAsync(clipboard, text);
        }
        catch { /* clipboard may be unavailable */ }
    }

    public async Task PasteAsync()
    {
        string? text;
        try
        {
            text = TopLevel.GetTopLevel(_view)?.Clipboard is { } clipboard
                ? await GetClipboardTextAsync(clipboard)
                : null;
        }
        catch { return; }
        if (string.IsNullOrEmpty(text))
            return;

        // The page decides the framing: a bracketed-paste mode enabled by the running program is
        // only known inside the emulator.
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        Invoke($"window.rkTerminal.paste('{payload}')");
    }

    private static async Task SetClipboardTextAsync(Avalonia.Input.Platform.IClipboard clipboard, string text)
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(DataFormat.Text, text));
        await clipboard.SetDataAsync(data);
    }

    private static async Task<string?> GetClipboardTextAsync(Avalonia.Input.Platform.IClipboard clipboard)
    {
        var transfer = await clipboard.TryGetDataAsync();
        if (transfer is null)
            return null;
        try { return await transfer.TryGetValueAsync(DataFormat.Text); }
        finally
        {
            if (transfer is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync();
            else (transfer as IDisposable)?.Dispose();
        }
    }

    public void SelectAll() => Invoke("window.rkTerminal.selectAll()");

    public void ClearScreen() => Invoke("window.rkTerminal.clear()");

    public void ApplyAppearance(TerminalSettingsDto appearance)
    {
        _appearance = appearance;
        ApplySurfaceBackground();
        SendOptions();
    }

    public void ApplyLabels(IReadOnlyDictionary<string, string> labels)
    {
        _labels = labels;
        SendOptions();
    }

    /// <summary>
    /// A WebView is an operating-system child window: it is not composed by Avalonia and would stay
    /// on top of every other managed window, so it is withdrawn whenever its window is not the
    /// active one.
    /// </summary>
    public void SetNativeVisible(bool visible) => _view.IsVisible = visible;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _pump.Stop();
        _view.WebMessageReceived -= OnWebMessageReceived;
        _view.Loaded -= OnViewLoaded;
        StopSession();
        _pageReady.TrySetResult(false);
        Invoke("window.rkTerminal.dispose()");
        lock (_bufferGate) _pending.Clear();
    }

    private void OnViewLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_navigated || _disposed)
            return;
        _navigated = true;
        try
        {
            _view.NavigateToString(XtermTerminalPage.Compose(BuildOptions()), XtermTerminalPage.BaseUri);
        }
        catch (Exception exception)
        {
            _pageReady.TrySetException(new TerminalSurfaceUnavailableException("The WebView could not host the terminal page.", exception));
        }
    }

    /// <summary>Waits for the page to announce itself, or reports that this host cannot run it.</summary>
    private async Task WaitForPageAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
            throw new TerminalSurfaceUnavailableException("The terminal surface is already closed.");

        var ready = await Task.WhenAny(_pageReady.Task, Task.Delay(PageReadyTimeout, cancellationToken)).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (ready != _pageReady.Task)
            _pageReady.TrySetException(new TerminalSurfaceUnavailableException("The terminal page did not start within the allowed time."));

        // Surfaces an exception raised by NavigateToString as well as a page that came up.
        await _pageReady.Task.ConfigureAwait(true);
    }

    private Dictionary<string, object?> BuildOptions() => DescribeOptions(_appearance, _labels);

    /// <summary>
    /// The option bag the page is told about before the session attaches. Kept as a pure function so the
    /// page contract can be checked on a machine that has no WebView to render it.
    /// </summary>
    internal static Dictionary<string, object?> DescribeOptions(TerminalSettingsDto appearance, IReadOnlyDictionary<string, string> labels) => new()
    {
        ["fontFamily"] = TerminalAppearance.ToCssFontStack(TerminalAppearance.ResolveFontFamily(appearance.FontFamily)),
        ["fontSize"] = Math.Clamp(appearance.FontSize, MinimumFontSize, MaximumFontSize),
        ["scrollback"] = ScrollbackLimit,
        ["theme"] = new Dictionary<string, string>
        {
            ["background"] = appearance.BackgroundColor,
            ["foreground"] = appearance.ForegroundColor,
            ["cursor"] = appearance.CursorColor,
            ["selectionBackground"] = appearance.ForegroundColor,
        },
        ["labels"] = labels,
    };

    private void ApplySurfaceBackground()
    {
        try { _view.Background = new SolidColorBrush(Color.Parse(_appearance.BackgroundColor)); }
        catch { /* an unparsable colour only costs the pre-render background */ }
    }

    private void SendOptions() => Invoke($"window.rkTerminal.setOptions({JsonSerializer.Serialize(BuildOptions(), RelaxKonOSJsonOptions.Default)})");

    private static async Task StopAsync(ITerminalTransport transport)
    {
        try { await transport.StopAsync(); } catch { /* best effort */ }
        transport.Dispose();
    }

    private void OnTransportData(byte[] buffer, int count)
    {
        if (count <= 0 || _disposed)
            return;

        lock (_bufferGate)
        {
            if (_pending.Count + count > MaximumPendingBytes)
                _pending.RemoveRange(0, _pending.Count + count - MaximumPendingBytes);
            for (var index = 0; index < count; index++)
                _pending.Add(buffer[index]);
        }

        // Output arrives on a transport thread; the page can only be driven from the UI thread.
        // Only one wake-up is queued at a time, so a fast producer cannot flood the dispatcher.
        if (Interlocked.Exchange(ref _wakeQueued, 1) == 0)
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _wakeQueued, 0);
                if (!_pumping)
                {
                    _pump.Start();
                    FlushPending();
                }
            });
    }

    private void OnTransportExited(int exitCode)
    {
        Dispatcher.UIThread.Post(() => ProcessExited?.Invoke(this, exitCode));
    }

    private void FlushPending()
    {
        if (_pumping || _disposed)
            return;
        _pumping = true;
        _ = FlushPendingAsync();
    }

    private async Task FlushPendingAsync()
    {
        try
        {
            while (!_disposed)
            {
                var chunk = TakePending();
                if (chunk is null)
                    return;
                await _view.InvokeScript($"window.rkTerminal.write('{Convert.ToBase64String(chunk)}')").ConfigureAwait(true);
            }
        }
        catch
        {
            // A page that cannot be scripted any more has already failed; the session itself stays
            // reachable through the window's own lifecycle.
        }
        finally
        {
            _pumping = false;
            lock (_bufferGate)
            {
                if (_pending.Count == 0)
                    _pump.Stop();
            }
        }
    }

    private byte[]? TakePending()
    {
        lock (_bufferGate)
        {
            if (_pending.Count == 0)
                return null;
            var length = Math.Min(_pending.Count, MaximumFlushBytes);
            var chunk = new byte[length];
            _pending.CopyTo(0, chunk, 0, length);
            _pending.RemoveRange(0, length);
            return chunk;
        }
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (_disposed || string.IsNullOrWhiteSpace(e.Body))
            return;

        try
        {
            using var message = JsonDocument.Parse(e.Body);
            var root = message.RootElement;
            if (!root.TryGetProperty("type", out var typeProperty))
                return;

            switch (typeProperty.GetString())
            {
                case "ready":
                    _pageReady.TrySetResult(true);
                    SendOptions();
                    FlushPending();
                    break;
                case "input":
                    if (root.TryGetProperty("data", out var data) && data.GetString() is { Length: > 0 } encoded)
                        _transport?.SendInput(Convert.FromBase64String(encoded));
                    break;
                case "resize":
                    OnPageResized(root);
                    break;
                case "title":
                    if (root.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } text)
                        TitleChanged?.Invoke(this, text);
                    break;
                case "selection":
                    _selection = root.TryGetProperty("text", out var selection) ? selection.GetString() ?? string.Empty : string.Empty;
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                    break;
                case "copy":
                    _ = CopySelectionAsync();
                    break;
                case "paste":
                    _ = PasteAsync();
                    break;
            }
        }
        catch (JsonException) { /* a malformed message must not take the terminal down */ }
        catch (FormatException) { /* ditto for a malformed payload */ }
    }

    private void OnPageResized(JsonElement message)
    {
        if (!message.TryGetProperty("columns", out var columns) || !message.TryGetProperty("rows", out var rows)
            || columns.TryGetInt32(out var columnCount) is false || rows.TryGetInt32(out var rowCount) is false
            || columnCount <= 0 || rowCount <= 0)
            return;

        var dimensions = new TerminalSessionDimensions(columnCount, rowCount,
            message.TryGetProperty("width", out var width) && width.TryGetInt32(out var widthPixels) ? widthPixels : 0,
            message.TryGetProperty("height", out var height) && height.TryGetInt32(out var heightPixels) ? heightPixels : 0);
        _requestedDimensions = dimensions;
        _transport?.Resize(dimensions);
    }

    private void Invoke(string script)
    {
        if (_disposed)
            return;
        try { _ = _view.InvokeScript(script); }
        catch { /* a page that is gone is reported by the session, not by chrome actions */ }
    }
}
