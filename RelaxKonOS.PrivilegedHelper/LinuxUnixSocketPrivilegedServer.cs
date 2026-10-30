using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Persistent local listener for the Linux Helper. One connection carries exactly one handshake and
/// one closed-set operation; progress and the terminal result stream back on the same connection.
/// The type never selects an operation itself: it delegates to the shared dispatcher so the socket
/// transport cannot widen the privileged surface.
/// </summary>
internal sealed class LinuxUnixSocketPrivilegedServer : IAsyncDisposable
{
    private const int MaximumRecentOperationIds = 10_000;
    private const int ListenBacklog = 64;
    private static readonly TimeSpan RecentOperationLifetime = TimeSpan.FromMinutes(10);

    private readonly LinuxPrivilegedHelperConfiguration _configuration;
    private readonly Func<PrivilegedOperationRequest, Func<PrivilegedOperationFrame, Task>?, Task<PrivilegedOperationResult>> _dispatch;
    private readonly IUnixSocketFileSystem _fileSystem;
    private readonly IHelperPeerAuthorizer _peerAuthorizer;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _recentOperationIds = new();
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private readonly SemaphoreSlim _requestSlots;
    private Socket? _listener;
    private Task? _acceptLoop;
    private int _stopped;

    public LinuxUnixSocketPrivilegedServer(LinuxPrivilegedHelperConfiguration configuration,
        Func<PrivilegedOperationRequest, Func<PrivilegedOperationFrame, Task>?, Task<PrivilegedOperationResult>> dispatch,
        IUnixSocketFileSystem fileSystem, IHelperPeerAuthorizer peerAuthorizer, Action<string> log)
    {
        configuration.Validate();
        _configuration = configuration;
        _dispatch = dispatch;
        _fileSystem = fileSystem;
        _peerAuthorizer = peerAuthorizer;
        _log = log;
        _requestSlots = new SemaphoreSlim(configuration.MaximumConcurrentRequests, configuration.MaximumConcurrentRequests);
    }

    public string SocketPath => _configuration.SocketPath;

    /// <summary>
    /// Binds synchronously so the host never reports readiness while a background listener is
    /// repeatedly failing to bind. A pre-existing path is removed only after it is proven to be a
    /// root-owned socket that nobody is listening on.
    /// </summary>
    public void Start()
    {
        if (_listener is not null) throw new InvalidOperationException("The Helper socket server has already started.");
        var directory = Path.GetDirectoryName(_configuration.SocketPath)
            ?? throw new InvalidOperationException("The Helper socket path has no parent directory.");
        EnsureRuntimeDirectory(directory);
        RemoveStaleSocket(_configuration.SocketPath);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(_configuration.SocketPath));
            listener.Listen(ListenBacklog);
            ApplySocketPermissions(_configuration.SocketPath);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
        _listener = listener;
        _acceptLoop = Task.Run(() => AcceptAsync(listener));
        _log($"event=started socket={_configuration.SocketPath} concurrency={_configuration.MaximumConcurrentRequests} "
            + $"authorizedUids={string.Join(',', _configuration.AllowedUserIds)}");
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _stopping.Cancel();
        try { _listener?.Dispose(); } catch (Exception exception) when (exception is SocketException or ObjectDisposedException) { }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException) { }
        }
        try { await Task.WhenAll(_connections.Keys).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException) { }
        // Only the socket this process created is removed. A different file at the path (for example
        // a replacement socket from a newer Helper) is left untouched.
        if (_fileSystem.Stat(_configuration.SocketPath) is { IsSocket: true } status
            && (status.OwnerId == 0 || status.OwnerId == _fileSystem.EffectiveUserId))
        {
            try { _fileSystem.Delete(_configuration.SocketPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log($"event=socket-remove-failed socket={_configuration.SocketPath} error={exception.GetType().Name}");
            }
        }
        _log("event=stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _requestSlots.Dispose();
        _stopping.Dispose();
    }

    private void EnsureRuntimeDirectory(string directory)
    {
        var status = _fileSystem.Stat(directory);
        if (status is null)
        {
            _fileSystem.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        else if (!status.Value.IsDirectory || status.Value.IsSymbolicLink)
        {
            throw new InvalidOperationException("The Helper socket directory is not a real directory.");
        }
        else
        {
            // A configured socket inside a world-writable directory could be replaced between our
            // checks and bind. Refuse instead of trying to repair an untrusted parent.
            if ((status.Value.Mode & UnixFileMode.OtherWrite) != 0)
                throw new InvalidOperationException("The Helper socket directory is writable by another account.");
            // systemd owns /run/relaxkonos. The Server group needs traversal only; listing stays denied,
            // while the socket's 0660 mode remains the authorization boundary.
            _fileSystem.SetMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        if (_configuration.SocketGroupId != 0)
        {
            try { _fileSystem.SetOwner(directory, _fileSystem.EffectiveUserId == 0 ? 0u : _fileSystem.EffectiveUserId, _configuration.SocketGroupId); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log($"event=directory-owner-failed directory={directory} error={exception.GetType().Name}");
            }
        }
    }

    private void RemoveStaleSocket(string path)
    {
        var status = _fileSystem.Stat(path);
        if (status is null) return;
        if (status.Value.IsSymbolicLink || status.Value.IsDirectory || !status.Value.IsSocket)
            throw new InvalidOperationException("Refusing to replace a path that is not the Helper socket.");
        if (status.Value.OwnerId != 0 && status.Value.OwnerId != _fileSystem.EffectiveUserId)
            throw new InvalidOperationException("Refusing to replace a socket owned by another account.");
        if (IsListening(path))
            throw new InvalidOperationException("Another privileged Helper is already listening on the configured socket.");
        _fileSystem.Delete(path);
    }

    private static bool IsListening(string path)
    {
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            probe.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (OperationCanceledException) { return true; }
        catch (Exception exception) when (exception is SocketException or IOException or ObjectDisposedException) { return false; }
    }

    private void ApplySocketPermissions(string path)
    {
        _fileSystem.SetMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        if (_configuration.SocketGroupId != 0)
            _fileSystem.SetOwner(path, _fileSystem.EffectiveUserId == 0 ? 0u : _fileSystem.EffectiveUserId, _configuration.SocketGroupId);
    }

    private async Task AcceptAsync(Socket listener)
    {
        while (!_stopping.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await listener.AcceptAsync(_stopping.Token); }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (_stopping.IsCancellationRequested) return; continue; }
            var connection = HandleAsync(socket);
            _connections[connection] = 0;
            _ = connection.ContinueWith(task => _connections.TryRemove(task, out _), TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(Socket socket)
    {
        try { await HandleConnectionAsync(socket, _stopping.Token); }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception exception)
        {
            // A single malformed or disconnected peer must never take the root service down.
            _log($"event=connection-failed error={exception.GetType().Name}");
        }
        finally
        {
            try { socket.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task HandleConnectionAsync(Socket socket, CancellationToken cancellationToken)
    {
        var peer = _peerAuthorizer.Authorize(socket);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        if (!peer.Allowed)
        {
            _log($"event=caller-rejected uid={peer.UserId} gid={peer.GroupId} pid={peer.ProcessId} reason={peer.Reason}");
            await WriteHandshakeAsync(stream, PrivilegedHelperHandshake.Rejected(PrivilegedProblemCode.AccessDenied,
                "the caller is not authorized"), cancellationToken);
            return;
        }
        if (!await _requestSlots.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            _log($"event=busy uid={peer.UserId}");
            await WriteHandshakeAsync(stream, PrivilegedHelperHandshake.Rejected(PrivilegedProblemCode.HelperUnavailable,
                "the Helper is at its concurrency limit"), cancellationToken);
            return;
        }
        try
        {
            var helloBytes = await PrivilegedHelperSocketFraming.ReadAsync(stream,
                PrivilegedHelperTransportProtocol.MaximumHandshakeFrameBytes, cancellationToken);
            if (helloBytes is null) return;
            PrivilegedHelperHandshake? hello;
            try { hello = JsonSerializer.Deserialize<PrivilegedHelperHandshake>(helloBytes); }
            catch (JsonException) { hello = null; }
            if (hello is null || hello.Type != PrivilegedHelperHandshake.HelloType || !hello.IsValid())
            {
                _log($"event=handshake-rejected uid={peer.UserId} reason=invalid-hello");
                await WriteHandshakeAsync(stream, PrivilegedHelperHandshake.Rejected(PrivilegedProblemCode.InvalidProtocol,
                    "the Helper transport handshake is invalid"), cancellationToken);
                return;
            }
            await WriteHandshakeAsync(stream, PrivilegedHelperHandshake.Welcome(), cancellationToken);

            var requestBytes = await PrivilegedHelperSocketFraming.ReadAsync(stream, PrivilegedHelperTransportProtocol.MaximumFrameBytes, cancellationToken);
            if (requestBytes is null) return;
            PrivilegedOperationRequest? request;
            try { request = JsonSerializer.Deserialize<PrivilegedOperationRequest>(requestBytes); }
            catch (JsonException) { request = null; }
            if (request is null)
            {
                await WriteResultAsync(stream, new(false, 64, Error: "invalid request", ProblemCode: PrivilegedProblemCode.InvalidRequest), cancellationToken);
                return;
            }
            if (request.Version != PrivilegedOperationProtocol.Version)
            {
                await WriteResultAsync(stream, new(false, 64, Error: "unsupported protocol version", ProblemCode: PrivilegedProblemCode.InvalidProtocol), cancellationToken);
                return;
            }
            if (request.OperationId is not { } operationId || operationId == Guid.Empty)
            {
                await WriteResultAsync(stream, new(false, 64, Error: "operation id is required", ProblemCode: PrivilegedProblemCode.InvalidRequest), cancellationToken);
                return;
            }
            if (request.Correlation is not { } correlation || !correlation.IsValid())
            {
                await WriteResultAsync(stream, new(false, 64, Error: "valid correlation metadata is required", ProblemCode: PrivilegedProblemCode.InvalidRequest), cancellationToken);
                return;
            }
            PruneRecentOperationIds();
            var durableReceipt = PrivilegedOperationReplayPolicy.UsesDurableReceipt(request.Operation);
            if (!durableReceipt && _recentOperationIds.Count >= MaximumRecentOperationIds)
            {
                await WriteResultAsync(stream, new(false, 69, Error: "operation replay cache is full", ProblemCode: PrivilegedProblemCode.HelperUnavailable), cancellationToken);
                return;
            }
            if (!durableReceipt && !_recentOperationIds.TryAdd(operationId, DateTimeOffset.UtcNow.Add(RecentOperationLifetime)))
            {
                await WriteResultAsync(stream, new(false, 17, Error: "operation id was already processed", ProblemCode: PrivilegedProblemCode.Conflict), cancellationToken);
                return;
            }

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = await _dispatch(request,
                frame => WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(frame), CancellationToken.None));
            await WriteResultAsync(stream, result, cancellationToken);
            _log($"event=request-completed operation={request.Operation} id={request.OperationId} uid={peer.UserId} "
                + $"success={result.Success} code={result.ProblemCode} durationMs={(long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds}");
        }
        finally
        {
            _requestSlots.Release();
        }
    }

    private void PruneRecentOperationIds()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _recentOperationIds.Where(pair => pair.Value <= now))
            _recentOperationIds.TryRemove(pair.Key, out _);
    }

    private static Task WriteHandshakeAsync(Stream stream, PrivilegedHelperHandshake handshake, CancellationToken cancellationToken) =>
        WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(handshake), cancellationToken);

    private static Task WriteResultAsync(Stream stream, PrivilegedOperationResult result, CancellationToken cancellationToken) =>
        WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(PrivilegedOperationFrame.Completed(result)), cancellationToken);

    private static Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken) =>
        PrivilegedHelperSocketFraming.WriteAsync(stream, payload, cancellationToken);
}
