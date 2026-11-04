using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>Tracks request completion, including cache use; does not queue or replay operations.</summary>
internal sealed class SystemPerformanceRequestLifetime(Action release)
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action? _release = release;
    private int _pending;
    private bool _stopping, _releasing;

    internal IDisposable Enter(CancellationToken ct)
    {
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (_stopping) throw new ModuleUnavailableException("module-host-stopping", 30, "Performance request admission is closed.");
            _pending++; return new Request(this);
        }
    }

    internal void BeginStop()
    {
        bool cleanup;
        lock (_gate) { _stopping = true; cleanup = ClaimRelease(); }
        if (cleanup) Release();
    }

    internal Task StopAsync(CancellationToken ct)
    { BeginStop(); return _drained.Task.WaitAsync(ct); }

    private void Exit()
    {
        bool cleanup;
        lock (_gate) { _pending--; cleanup = ClaimRelease(); }
        if (cleanup) Release();
    }

    private bool ClaimRelease()
    {
        if (!_stopping || _pending != 0 || _releasing) return false;
        _releasing = true; return true;
    }

    private void Release()
    {
        try { _release!(); _release = null; _drained.TrySetResult(); }
        catch (Exception error) { _drained.TrySetException(error); _ = _drained.Task.Exception; }
    }

    private sealed class Request(SystemPerformanceRequestLifetime owner) : IDisposable
    {
        private SystemPerformanceRequestLifetime? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
    }
}
