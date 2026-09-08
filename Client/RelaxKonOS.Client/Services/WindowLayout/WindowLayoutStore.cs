using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Services.WindowLayout;

/// <summary>
/// Keeps a local in-memory copy of window dimensions and syncs it to the connected workspace
/// after a short idle period. A disconnect also makes one final best-effort flush.
/// </summary>
public sealed class WindowLayoutStore : IWindowLayoutStore, IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly IAuthSession _session;
    private readonly IWindowLayoutClient _client;
    private readonly Dictionary<string, Size> _sizes = new(StringComparer.Ordinal);
    private CancellationTokenSource? _saveDelayCts;
    private Connection? _lastConnection;
    private long _version;
    private bool _dirty;

    public WindowLayoutStore(IAuthSession session, IWindowLayoutClient client)
    {
        _session = session;
        _client = client;
        _session.StateChanged += OnSessionStateChanged;
        _ = LoadIfAuthenticatedAsync();
    }

    public Size? GetSize(string key)
    {
        lock (_gate)
            return _sizes.TryGetValue(key, out var size) ? size : null;
    }

    public void RecordSize(string key, Size size)
    {
        if (string.IsNullOrWhiteSpace(key) || size.Width <= 0 || size.Height <= 0)
            return;

        lock (_gate)
        {
            _sizes[key] = size;
            _dirty = true;
            _version++;
            ScheduleSave_NoLock();
        }
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        Connection? connection;
        lock (_gate)
        {
            _saveDelayCts?.Cancel();
            connection = CurrentConnection() ?? _lastConnection;
        }
        if (connection is not null)
            await SaveAsync(connection, ct);
    }

    private void OnSessionStateChanged(object? sender, AuthSessionStateChangedEventArgs e)
    {
        if (e.State == AuthSessionState.Authenticated)
            _ = LoadIfAuthenticatedAsync();
        else if (e.State == AuthSessionState.Unauthenticated)
            _ = FlushAsync();
    }

    private async Task LoadIfAuthenticatedAsync()
    {
        var connection = CurrentConnection();
        if (connection is null)
            return;

        try
        {
            var layouts = await _client.GetAsync(connection.ServerUrl, connection.AccessToken, connection.WorkspaceId);
            lock (_gate)
            {
                _lastConnection = connection;
                var loaded = layouts.Windows
                    .Where(layout => !string.IsNullOrWhiteSpace(layout.Key) && layout.Width > 0 && layout.Height > 0)
                    .ToDictionary(layout => layout.Key, layout => new Size(layout.Width, layout.Height), StringComparer.Ordinal);
                if (!_dirty)
                    _sizes.Clear();
                foreach (var layout in loaded)
                {
                    if (!_dirty || !_sizes.ContainsKey(layout.Key))
                        _sizes[layout.Key] = layout.Value;
                }
            }
        }
        catch
        {
            // Keep local dimensions usable when a server has not yet been upgraded.
        }
    }

    private void ScheduleSave_NoLock()
    {
        _saveDelayCts?.Cancel();
        var cts = _saveDelayCts = new CancellationTokenSource();
        _ = SaveWhenIdleAsync(cts);
    }

    private async Task SaveWhenIdleAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(SaveDelay, cts.Token);
            var connection = CurrentConnection();
            if (connection is not null)
                await SaveAsync(connection, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer resize resets the idle interval.
        }
        catch
        {
            // Persistence is best effort; the in-memory layout is retained for this session.
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_saveDelayCts, cts))
                    _saveDelayCts = null;
            }
            cts.Dispose();
        }
    }

    private async Task SaveAsync(Connection connection, CancellationToken ct = default)
    {
        await _saveGate.WaitAsync(ct);
        try
        {
            WindowSizeDto[] snapshot;
            long version;
            lock (_gate)
            {
                if (!_dirty)
                    return;
                version = _version;
                snapshot = _sizes.Select(x => new WindowSizeDto(x.Key, x.Value.Width, x.Value.Height)).ToArray();
            }

            await _client.SaveAsync(connection.ServerUrl, connection.AccessToken, connection.WorkspaceId,
                new WorkspaceWindowLayoutDto(snapshot), ct);

            lock (_gate)
            {
                _lastConnection = connection;
                if (_version == version)
                    _dirty = false;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private Connection? CurrentConnection()
        => _session is { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } workspace }
            ? new Connection(url, tokens.AccessToken, workspace.Id)
            : null;

    public void Dispose()
    {
        _session.StateChanged -= OnSessionStateChanged;
        lock (_gate)
            _saveDelayCts?.Cancel();
        _saveGate.Dispose();
    }

    private sealed record Connection(string ServerUrl, string AccessToken, Guid WorkspaceId);
}
