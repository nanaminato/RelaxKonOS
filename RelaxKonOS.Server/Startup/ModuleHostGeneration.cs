using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.Startup;

/// <summary>A resident process generation and all proxy/callback work using its identity.</summary>
internal sealed class ModuleHostGeneration(string moduleId, string serviceToken, IModuleWorkLease processLease,
    Func<Task> shutdown, Action closing, Action<Exception?> completed)
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _close = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationToken _token = processLease.Stopping;
    private CancellationTokenRegistration _registration;
    private Task? _runner;
    private Uri? _endpoint;
    private int _pending;
    private TaskCompletionSource? _idle;
    private bool _closing;
    private Exception? _notificationFailure;

    internal Guid Id { get; } = Guid.NewGuid();
    internal string ModuleId => moduleId;
    internal string ServiceToken => serviceToken;
    internal Task Completion => _done.Task;
    internal CancellationToken Stopping => _stopping.Token;
    internal void Start()
    {
        _registration = _token.Register(RequestClose);
        _runner = CloseCoreAsync();
    }
    internal bool PublishReady(Uri endpoint, Action? beforePublish = null)
    {
        lock (_gate)
        {
            if (_closing) return false;
            beforePublish?.Invoke();
            _endpoint = endpoint;
            return true;
        }
    }
    internal void StartupFinished() => _startup.TrySetResult();
    internal ModuleHostConnection? TryAcquire()
    {
        lock (_gate)
        {
            if (_closing || _endpoint is null || _token.IsCancellationRequested) return null;
            _pending++; return new(this, moduleId, Id, _endpoint, serviceToken, Stopping);
        }
    }
    internal void RequestClose()
    {
        lock (_gate)
        {
            if (_closing) return;
            _closing = true;
            if (_pending == 0) _drained.TrySetResult();
        }
        // No user/transport callback under the generation admission lock.
        try { closing(); }
        catch (Exception error) { _notificationFailure = error; }
        try { _stopping.Cancel(); }
        catch (Exception error) { _notificationFailure ??= error; }
        finally { _close.TrySetResult(); }
    }
    internal Task WaitForIdleAsync()
    {
        lock (_gate)
            return _pending == 0 ? Task.CompletedTask : (_idle ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    internal void Release()
    {
        lock (_gate)
        {
            if (--_pending != 0) return;
            _idle?.TrySetResult();
            _idle = null;
            if (_closing) _drained.TrySetResult();
        }
    }
    private async Task CloseCoreAsync()
    {
        try
        {
            await _close.Task;
            await _startup.Task;
            await _drained.Task;
            await shutdown();
            if (_notificationFailure is not null) throw _notificationFailure;
            processLease.Dispose();
            _registration.Dispose(); _stopping.Dispose();
            completed(null);
            _done.TrySetResult();
        }
        catch (Exception error)
        {
            // Unknown cleanup keeps the process lease and generation; there is no automatic replay.
            _registration.Dispose();
            processLease.RequireRecovery();
            completed(error);
            _done.TrySetException(error); _ = _done.Task.Exception;
        }
    }
}

internal sealed class ModuleHostConnection(ModuleHostGeneration owner, string moduleId, Guid generationId,
    Uri endpoint, string serviceToken, CancellationToken stopping) : IDisposable
{
    private ModuleHostGeneration? _owner = owner;
    internal string ModuleId => moduleId;
    internal Guid GenerationId => generationId;
    internal Uri Endpoint => endpoint;
    internal string ServiceToken => serviceToken;
    internal CancellationToken Stopping => stopping;
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
}
