namespace RelaxKonOS.Server.Residency;

/// <summary>Owns admitted background work through actual execution and terminal persistence.</summary>
internal sealed class ResidentBackgroundWorkOwner(IResidentModuleWorkLeaseSource leases, string moduleId) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Work> _work = new();
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _admissionsDrained = Completed();
    private int _admitting;
    private bool _closed;

    internal bool HasResponsibility { get { lock (_gate) return _admitting != 0 || _work.Count != 0; } }
    internal int WorkCount { get { lock (_gate) return _work.Count; } }
    internal async ValueTask<Work> AdmitAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_closed) throw new ModuleUnavailableException("module-host-stopping", 30, "Background work admission is closed.");
            ct.ThrowIfCancellationRequested();
            if (_admitting++ == 0) _admissionsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        IModuleWorkLease? lease = null;
        try
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            lease = await leases.AcquireAsync(moduleId, waiting.Token);
            waiting.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_closed) throw new ModuleUnavailableException("module-host-stopping", 30, "Background work admission is closed.");
                var work = new Work(this, lease, _stop.Token);
                _work.Add(work.Id, work); lease = null; return work;
            }
        }
        finally
        {
            lease?.Dispose();
            lock (_gate) if (--_admitting == 0) _admissionsDrained.TrySetResult();
        }
    }

    internal void BeginStop()
    {
        lock (_gate) { if (_closed) return; _closed = true; }
        _stop.Cancel();
    }
    internal async Task StopAsync(CancellationToken ct)
    {
        BeginStop();
        Task admissions; lock (_gate) admissions = _admissionsDrained.Task;
        await admissions.WaitAsync(ct);
        Task[] work; lock (_gate) work = _work.Values.Select(w => w.Completion).ToArray();
        await Task.WhenAll(work).WaitAsync(ct);
    }
    public void Dispose() => BeginStop();
    private static TaskCompletionSource Completed()
    { var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); source.SetResult(); return source; }

    internal sealed class Work : IModuleWorkLease
    {
        private readonly ResidentBackgroundWorkOwner _owner;
        private readonly IModuleWorkLease _lease;
        private readonly CancellationTokenSource _stopping;
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _published;
        private int _failed;
        private Task? _runner;
        internal Guid Id { get; } = Guid.NewGuid();
        internal Task Completion => _done.Task;
        internal CancellationToken Token => _stopping.Token;
        CancellationToken IModuleWorkLease.Stopping => Token;
        void IModuleWorkLease.RequireRecovery() => Fail(new InvalidOperationException("Resident request cleanup could not be confirmed."));
        internal Work(ResidentBackgroundWorkOwner owner, IModuleWorkLease lease, CancellationToken stopping)
        { _owner = owner; _lease = lease; _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping, lease.Stopping); }
        internal void Run(Func<CancellationToken, Task> action)
        {
            if (Interlocked.Exchange(ref _published, 1) != 0) throw new InvalidOperationException("Work was already published.");
            // Registration precedes scheduling; cancellation must not skip the cleanup/terminal owner task.
            _runner = Task.Run(() => ExecuteAsync(action), CancellationToken.None);
        }
        internal void Fail(Exception error)
        {
            Interlocked.Exchange(ref _published, 1);
            if (Interlocked.Exchange(ref _failed, 1) == 0)
            {
                try { _lease.RequireRecovery(); }
                catch (Exception recoveryError) { error = new AggregateException(error, recoveryError); }
            }
            _done.TrySetException(error); _ = _done.Task.Exception;
        }
        private async Task ExecuteAsync(Func<CancellationToken, Task> action)
        {
            try { await action(Token); Complete(); }
            catch (Exception error) { Fail(error); }
        }
        private void Complete()
        {
            _stopping.Dispose(); _lease.Dispose();
            lock (_owner._gate) _owner._work.Remove(Id);
            _done.TrySetResult();
        }
        public void Dispose()
        { if (Interlocked.CompareExchange(ref _published, 1, 0) == 0) Complete(); }
    }
}

internal sealed class ResidentWorkCompletionException(Exception inner) : InvalidOperationException("Background work terminal persistence failed.", inner);
