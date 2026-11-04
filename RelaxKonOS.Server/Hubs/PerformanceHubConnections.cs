using Microsoft.AspNetCore.SignalR;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.SystemPerformance;

namespace RelaxKonOS.Server.Hubs;

/// <summary>Owns connected clients, serialized group operations and their actual cleanup tasks.</summary>
public sealed class PerformanceHubConnections : IHostedService, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly SystemPerformanceRequestLifetime _requests;
    private readonly ISystemPerformanceWorkLeaseSource _leases;
    private readonly PerformanceSubscriptionRegistry _subscriptions;
    private int _stopping;

    public PerformanceHubConnections(ISystemPerformanceWorkLeaseSource leases, PerformanceSubscriptionRegistry subscriptions)
    {
        _leases = leases; _subscriptions = subscriptions; _shutdownToken = _shutdown.Token;
        _requests = new(_shutdown.Dispose);
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    internal async Task ConnectAsync(string id, IGroupManager groups, Action abort, CancellationToken ct)
    {
        var request = _requests.Enter(ct);
        IModuleWorkLease? work = null;
        CancellationTokenSource? stopping = null;
        try
        {
            ThrowIfStopping();
            stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownToken);
            work = await _leases.AcquireAsync(stopping.Token);
            Connection connection;
            lock (_gate)
            {
                ThrowIfStopping();
                if (_connections.ContainsKey(id)) throw new HubException("Performance connection already exists.");
                connection = new Connection(this, id, groups, abort, request, work, ct, _shutdownToken);
                _connections.Add(id, connection);
            }
            request = null!; work = null;
            connection.Start();
            connection.ThrowIfClosed();
        }
        finally { stopping?.Dispose(); work?.Dispose(); request?.Dispose(); }
    }

    internal Task SubscribeAsync(string id) => Find(id).SubscribeAsync();
    internal Task UnsubscribeAsync(string id) => Find(id).UnsubscribeAsync();
    internal Task DisconnectAsync(string id)
    {
        Connection? connection; lock (_gate) _connections.TryGetValue(id, out connection);
        if (connection is null) return Task.CompletedTask;
        connection.Close(); return connection.Completion;
    }

    private Connection Find(string id)
    {
        lock (_gate)
        {
            ThrowIfStopping();
            return _connections.TryGetValue(id, out var connection) ? connection : throw new HubException("Performance connection is closed.");
        }
    }
    private void ThrowIfStopping()
    { if (Volatile.Read(ref _stopping) != 0) throw new ModuleUnavailableException("module-host-stopping", 30, "Performance connections are closing."); }

    private void BeginStop()
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 0)
        {
            try { _shutdown.Cancel(); }
            finally { _requests.BeginStop(); }
        }
    }
    public async Task StopAsync(CancellationToken ct)
    {
        BeginStop();
        Task[] connections; lock (_gate) connections = _connections.Values.Select(c => c.Completion).ToArray();
        await Task.WhenAll(connections).WaitAsync(ct);
        await _requests.StopAsync(ct);
    }
    public void Dispose() => BeginStop();

    private sealed class Connection
    {
        private readonly PerformanceHubConnections _owner;
        private readonly string _id;
        private readonly IGroupManager _groups;
        private readonly Action _abort;
        private readonly IDisposable _request;
        private readonly IModuleWorkLease _work;
        private readonly CancellationTokenSource _stopping;
        private readonly CancellationToken _token;
        private readonly SemaphoreSlim _operations = new(1, 1);
        private readonly TaskCompletionSource _close = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _runner;
        private bool _mayBeMember, _registered;
        private Exception? _removeFailure;

        internal Connection(PerformanceHubConnections owner, string id, IGroupManager groups, Action abort,
            IDisposable request, IModuleWorkLease work, CancellationToken connection, CancellationToken host)
        {
            _owner = owner; _id = id; _groups = groups; _abort = abort; _request = request; _work = work;
            _stopping = CancellationTokenSource.CreateLinkedTokenSource(connection, host, work.Stopping);
            _token = _stopping.Token;
        }
        internal Task Completion => _done.Task;
        internal void Start() => _runner = RunAsync();
        internal void Close() => _close.TrySetResult();
        internal void ThrowIfClosed()
        {
            _token.ThrowIfCancellationRequested();
            if (_close.Task.IsCompleted) throw new HubException("Performance connection is closing.");
        }
        internal async Task SubscribeAsync()
        {
            await _operations.WaitAsync(_token);
            try
            {
                ThrowIfClosed();
                if (_registered) return;
                _mayBeMember = true;
                await _groups.AddToGroupAsync(_id, PerformanceHub.GroupName, _token);
                ThrowIfClosed();
                _registered = true;
                _owner._subscriptions.Subscribe(_id);
            }
            catch { Close(); throw; }
            finally { _operations.Release(); }
        }
        internal async Task UnsubscribeAsync()
        {
            await _operations.WaitAsync(_token);
            try
            {
                ThrowIfClosed();
                if (!_mayBeMember) return;
                try { await _groups.RemoveFromGroupAsync(_id, PerformanceHub.GroupName, CancellationToken.None); }
                catch (Exception error) { _removeFailure = error; throw; }
                _mayBeMember = false;
                Unregister();
            }
            catch { Close(); throw; }
            finally { _operations.Release(); }
        }
        private void Unregister()
        {
            if (!_registered) return;
            _registered = false; _owner._subscriptions.Unsubscribe(_id);
        }
        private async Task RunAsync()
        {
            try
            {
                try { await _close.Task.WaitAsync(_token); }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
                Close();
                Exception? abortFailure = null;
                try { _abort(); }
                catch (Exception error) { abortFailure = error; }
                await _operations.WaitAsync();
                try
                {
                    Unregister();
                    if (_removeFailure is not null) throw _removeFailure;
                    if (_mayBeMember)
                    {
                        await _groups.RemoveFromGroupAsync(_id, PerformanceHub.GroupName, CancellationToken.None);
                        _mayBeMember = false;
                    }
                }
                finally { _operations.Release(); }
                if (abortFailure is not null) throw abortFailure;
                _stopping.Dispose();
                _work.Dispose(); _request.Dispose();
                lock (_owner._gate) _owner._connections.Remove(_id);
                _done.TrySetResult();
            }
            catch (Exception error)
            {
                // Keep this entry, lease and failed cleanup responsibility; do not replay an unknown group operation.
                _done.TrySetException(error); _ = _done.Task.Exception;
            }
        }
    }
}
