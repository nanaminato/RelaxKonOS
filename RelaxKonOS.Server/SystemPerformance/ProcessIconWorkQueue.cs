namespace RelaxKonOS.Server.SystemPerformance;

internal readonly record struct ProcessIconWorkProtection(IDisposable Lease, CancellationToken Stopping);

/// <summary>Bounded read-only icon work. Cancellation never detaches an already started operation.</summary>
internal sealed class ProcessIconWorkQueue(Action release)
{
    internal const int MaximumParallel = 4;
    internal const int MaximumWaiting = 32;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _slots = new(MaximumParallel, MaximumParallel);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action? _release = release;
    private int _pending, _active;
    private bool _stopping, _releasing;

    internal (int Active, int Pending, bool Stopping) Describe()
    { lock (_gate) return (_active, _pending, _stopping); }

    internal Task<ProcessIconContent?> RunAsync(Func<Task<ProcessIconContent?>> operation, CancellationToken ct,
        Func<CancellationToken, ValueTask<ProcessIconWorkProtection>>? acquireProtection = null)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_stopping || _pending >= MaximumParallel + MaximumWaiting) return Task.FromResult<ProcessIconContent?>(null);
            _pending++;
        }
        return RunCoreAsync(operation, ct, acquireProtection);
    }

    private async Task<ProcessIconContent?> RunCoreAsync(Func<Task<ProcessIconContent?>> operation, CancellationToken ct,
        Func<CancellationToken, ValueTask<ProcessIconWorkProtection>>? acquireProtection)
    {
        var acquired = false;
        var active = false;
        ProcessIconWorkProtection? protection = null;
        try
        {
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token))
            {
                if (acquireProtection is not null) protection = await acquireProtection(waiting.Token).ConfigureAwait(false);
                using var protectedWaiting = protection is { } owned
                    ? CancellationTokenSource.CreateLinkedTokenSource(waiting.Token, owned.Stopping) : null;
                await _slots.WaitAsync(protectedWaiting?.Token ?? waiting.Token).ConfigureAwait(false);
            }
            acquired = true;
            ct.ThrowIfCancellationRequested();
            if (protection is { } protectedWork) protectedWork.Stopping.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_stopping) return null;
                _active++; active = true;
            }
            var result = await operation().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when ((_stop.IsCancellationRequested || protection?.Stopping.IsCancellationRequested == true)
            && !ct.IsCancellationRequested) { return null; }
        finally
        {
            try { protection?.Lease.Dispose(); }
            finally
            {
                bool cleanup;
                lock (_gate)
                {
                    if (active) _active--;
                    _pending--;
                    cleanup = ClaimRelease();
                }
                if (acquired) _slots.Release();
                if (cleanup) Release();
            }
        }
    }

    internal void BeginStop()
    {
        bool cancel, cleanup;
        lock (_gate)
        {
            cancel = !_stopping; _stopping = true;
            cleanup = ClaimRelease();
        }
        if (cancel) _stop.Cancel(); // Cancellation callbacks run outside the admission lock.
        if (cleanup) Release();
    }

    internal Task StopAsync(CancellationToken ct)
    { BeginStop(); return _drained.Task.WaitAsync(ct); }

    private bool ClaimRelease()
    {
        if (!_stopping || _pending != 0 || _releasing) return false;
        _releasing = true; return true;
    }

    private void Release()
    {
        try
        {
            _release!(); _release = null;
            _drained.TrySetResult();
        }
        catch (Exception error)
        {
            // Do not replay a partially failed cleanup or discard its owner on a later Stop call.
            _drained.TrySetException(error); _ = _drained.Task.Exception;
        }
    }
}
