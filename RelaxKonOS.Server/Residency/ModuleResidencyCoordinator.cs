using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Residency;

/// <summary>
/// Owns the residency state machine of every releasable module:
/// <c>Dormant → Loading → Active → Draining → Unloading → Dormant</c>, with <c>Faulted</c> on failure.
///
/// <para>
/// The coordinator never loads a module to answer a status query. It loads on a business lease, merges
/// concurrent first requests into one load, refuses new leases while a module drains, and never calls
/// into an object it has already disposed. Reclamation and lease admission share one lock, so a lease
/// can not slip in between "no leases" and "dispose".
/// </para>
/// </summary>
internal sealed class ModuleResidencyCoordinator : IAsyncDisposable
{
    /// <summary>Back-off after a failed load. A retry storm must not turn one broken module into an outage.</summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>Sessions remembered per module; long enough for a real device set, bounded on purpose.</summary>
    private const int MaxRememberedSessions = 128;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly IServiceProvider _services;
    private readonly ServerModuleContext _moduleContext;
    private readonly ILogger<ModuleResidencyCoordinator> _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _sweepInterval;
    private readonly IHttpContextAccessor? _http;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ModuleResidencyOptions _options;
    private CancellationTokenSource _shutdown;
    private Task? _sweepLoop;
    private bool _started;
    private int _waiting;
    private int _loading;
    private readonly Entry[] _releaseOrder;

    internal ModuleResidencyCoordinator(
        IReadOnlyList<IServerModuleRuntimeFactory> factories,
        ModuleResidencyOptions options,
        IServiceProvider services,
        ServerModuleContext moduleContext,
        ILogger<ModuleResidencyCoordinator> logger,
        TimeProvider? time = null,
        TimeSpan? sweepInterval = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? dependencies = null,
        IReadOnlySet<string>? residentRequiredModuleIds = null)
    {
        _options = options;
        _services = services;
        _moduleContext = moduleContext;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _sweepInterval = sweepInterval ?? TimeSpan.FromSeconds(1);
        _http = services.GetService(typeof(IHttpContextAccessor)) as IHttpContextAccessor;
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        foreach (var factory in factories)
            if (string.IsNullOrWhiteSpace(factory.ModuleId) || !_entries.TryAdd(factory.ModuleId, new Entry(factory, options.For(factory.ModuleId))))
                throw new InvalidOperationException("Duplicate or invalid module runtime factory.");
        if (dependencies?.Keys.Any(id => !_entries.ContainsKey(id)) == true
            || residentRequiredModuleIds?.Any(id => !_entries.ContainsKey(id)) == true)
            throw new InvalidOperationException("Unknown module in runtime dependency graph.");
        foreach (var entry in _entries.Values)
        {
            entry.Dependencies = dependencies?.GetValueOrDefault(entry.ModuleId)?.ToArray() ?? [];
            if (entry.Dependencies.Any(id => !_entries.ContainsKey(id)) || entry.Dependencies.Distinct(StringComparer.Ordinal).Count() != entry.Dependencies.Length)
                throw new InvalidOperationException("Invalid module runtime dependency.");
            entry.ResidentRequired = residentRequiredModuleIds?.Contains(entry.ModuleId) == true;
        }
        List<Entry> ordered = [];
        var visited = new Dictionary<string, bool>(StringComparer.Ordinal);
        void Visit(Entry entry)
        {
            if (visited.TryGetValue(entry.ModuleId, out var completed))
            { if (!completed) throw new InvalidOperationException("Circular module runtime dependency."); return; }
            visited[entry.ModuleId] = false;
            foreach (var dependency in entry.Dependencies.Order(StringComparer.Ordinal)) Visit(_entries[dependency]);
            visited[entry.ModuleId] = true;
            ordered.Add(entry);
        }
        foreach (var entry in _entries.Values.OrderBy(item => item.ModuleId, StringComparer.Ordinal)) Visit(entry);
        _releaseOrder = ordered.AsEnumerable().Reverse().ToArray();
    }

    internal IReadOnlyList<string> ModuleIds => _entries.Keys.ToArray();

    internal bool IsReleasable(string moduleId) => _entries.ContainsKey(moduleId);

    internal string DescribeSettings() => _options.Describe();

    /// <summary>
    /// Starts the idle sweep. Loading is still demand driven; this only decides when an already idle
    /// module may be released.
    /// </summary>
    internal void Start(CancellationToken applicationStopping)
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            var wasLifetime = _shutdown;
            _shutdown = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
            wasLifetime.Dispose();
        }

        _sweepLoop = Task.Run(() => SweepLoopAsync(_shutdown.Token));
    }

    /// <summary>
    /// Takes a lease, loading the module runtime if needed. The returned lease must be disposed only
    /// after the call it protects has finished, including the body of a streamed response.
    /// </summary>
    internal Task<ModuleLease> AcquireAsync(string moduleId, CancellationToken cancellationToken = default) =>
        AcquireCoreAsync(moduleId, cancellationToken, dependency: false);

    private async Task<ModuleLease> AcquireCoreAsync(string moduleId, CancellationToken cancellationToken, bool dependency)
    {
        var entry = EntryFor(moduleId);
        var waiting = false;
        try
        {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(entry.Settings.WakeTimeout);

        while (true)
        {
            Task<IServerModuleRuntime>? loading = null;
            Task? stateChanged = null;
            ModuleLease? lease = null;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_shutdown.IsCancellationRequested) throw Stopping(moduleId);
                if (!waiting && entry.State != ModuleResidencyState.Active)
                {
                    if (!dependency && _waiting >= _options.MaxWaitingTotal || entry.Waiting >= _options.MaxWaitingPerModule)
                        throw Overloaded(moduleId);
                    waiting = true; if (!dependency) _waiting++; entry.Waiting++;
                }
                switch (entry.State)
                {
                    case ModuleResidencyState.Active:
                        if (entry.Runtime is null) throw Stopping(moduleId);
                        entry.Leases++;
                        entry.IdleSince = null;
                        entry.ReleaseRequested = false;
                        entry.LastUsedAt = Now;
                        RememberSessionLocked(entry);
                        lease = new ModuleLease(this, entry.Runtime);
                        break;
                    case ModuleResidencyState.Loading:
                        loading = entry.Load;
                        break;
                    case ModuleResidencyState.Dormant:
                        loading = StartLoadLocked(entry);
                        break;
                    case ModuleResidencyState.Faulted:
                        if (Now < entry.RetryAfter) throw Unavailable(entry);
                        loading = StartLoadLocked(entry);
                        break;
                    default:
                        // Draining / Unloading: the module will be dormant shortly. Wait for that
                        // transition instead of reviving the instance that is being released.
                        stateChanged = entry.StateChanged.Task;
                        break;
                }
            }

            if (lease is not null) return lease;

            if (loading is not null)
            {
                try { await loading.WaitAsync(timeout.Token).ConfigureAwait(false); }
                catch (TimeoutException) { throw WakeTimeout(entry); }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw WakeTimeout(entry, exception);
                }
                catch (OperationCanceledException) { throw; }
                catch (ModuleUnavailableException) { /* Faulted; the next pass reports the reason. */ }
                continue;
            }

            if (stateChanged is not null)
            {
                try { await stateChanged.WaitAsync(timeout.Token).ConfigureAwait(false); }
                catch (TimeoutException) { throw WakeTimeout(entry); }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw WakeTimeout(entry, exception);
                }
                catch (OperationCanceledException) { throw; }
            }
        }
        }
        finally
        {
            if (waiting) lock (_gate) { if (!dependency) _waiting--; entry.Waiting--; }
        }
    }

    /// <summary>
    /// Synchronous variant for a cross-domain port that is not async (deployment asks whether a build is
    /// deployable). It fails closed instead of blocking a request indefinitely.
    /// </summary>
    internal ModuleLease? TryAcquire(string moduleId, TimeSpan timeout)
    {
        if (!_entries.ContainsKey(moduleId)) return null;
        try
        {
            using var cancellation = new CancellationTokenSource(timeout);
            return AcquireAsync(moduleId, cancellation.Token).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is ModuleUnavailableException or OperationCanceledException)
        {
            return null;
        }
    }

    internal void Release(string moduleId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(moduleId, out var entry)) return;
            if (entry.Leases > 0) entry.Leases--;
            if (entry.Leases == 0) entry.IdleSince = Now;
        }
    }

    /// <summary>
    /// Records that a session used a module. Logout uses this to release only modules that session used,
    /// never to make a global decision from one user leaving.
    /// </summary>
    internal void OnSessionEnded(Guid sessionId)
    {
        var key = sessionId.ToString("D");
        List<Entry> touched = [];
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                if (!entry.Sessions.Remove(key)) continue;
                if (entry.Settings.Strategy != ModuleResidencyStrategy.ReleaseOnLogout) continue;
                if (entry.State != ModuleResidencyState.Active) continue;
                entry.ReleaseRequested = true;
                touched.Add(entry);
            }
        }

        if (touched.Count > 0)
        {
            // Logout is a trigger, not a command: the release still runs through the same sweep that
            // checks leases, so a second device using the module keeps it alive.
            _ = Task.Run(async () =>
            {
                try { await SweepAsync(_shutdown.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception exception) { _logger.LogError(exception, "Releasing modules after session logout failed"); }
            });
        }
    }

    /// <summary>Release attempt for every module whose grace period has expired. Safe to call directly.</summary>
    internal async Task SweepAsync(CancellationToken cancellationToken = default)
    {
        Entry[] due;
        lock (_gate)
        {
            if (_shutdown.IsCancellationRequested) return;
            var now = Now;
            List<Entry> candidates = [];
            foreach (var entry in _entries.Values)
            {
                if (entry.State != ModuleResidencyState.Active || entry.Leases != 0) continue;
                if (!entry.Settings.IsReleasable || entry.ResidentRequired) continue;
                if (entry.ReleaseRequested)
                {
                    candidates.Add(entry);
                    continue;
                }

                entry.IdleSince ??= now;
                if (now - entry.IdleSince.Value >= entry.Settings.IdleGracePeriod) candidates.Add(entry);
            }

            due = _releaseOrder.Where(candidates.Contains).ToArray();
        }

        foreach (var entry in due)
        {
            try { await TryReleaseAsync(entry, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Releasing idle server module {ModuleId} failed", entry.ModuleId);
            }
        }
    }

    /// <summary>
    /// State of every module that declares a release boundary. Reads nothing from the module itself, so
    /// a status query can not wake it.
    /// </summary>
    internal ModuleResidencySnapshot? Describe(string moduleId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(moduleId, out var entry)) return null;
            return SnapshotLocked(entry);
        }
    }

    internal IReadOnlyList<ModuleResidencySnapshot> DescribeAll()
    {
        lock (_gate)
        {
            return _entries.Values.Select(SnapshotLocked).ToArray();
        }
    }

    internal async Task<string?> TrySleepAsync(string moduleId, CancellationToken cancellationToken)
    {
        var entry = EntryFor(moduleId);
        lock (_gate)
        {
            if (_shutdown.IsCancellationRequested) return "module-host-stopping";
            if (entry.State == ModuleResidencyState.Dormant) return null;
            if (entry.ResidentRequired) return "module-resident-dependent";
            if (!entry.Settings.IsReleasable) return "module-policy-keeps-loaded";
            if (entry.State == ModuleResidencyState.Faulted) return "module-recovery-blocked";
            if (entry.State != ModuleResidencyState.Active || entry.Leases != 0) return "module-active-work";
        }
        return await TryReleaseAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        Task[] loads;
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested) return;
            _lifetime.Cancel();
            _shutdown.Cancel();
            entries = _releaseOrder;
            loads = entries.Select(entry => entry.Load).OfType<Task>().ToArray();
        }

        try { if (_sweepLoop is not null) await _sweepLoop.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
        catch (Exception exception) { _logger.LogWarning(exception, "Module sweep did not finish cleanly during shutdown"); }

        try { await Task.WhenAll(loads).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
        catch (Exception exception) { _logger.LogWarning(exception, "Module loads did not all finish cleanly during shutdown"); }

        foreach (var entry in entries)
        {
            IServerModuleRuntime? runtime;
            lock (_gate)
            {
                if (entry.Leases != 0 || entry.State is ModuleResidencyState.Draining or ModuleResidencyState.Unloading)
                {
                    _logger.LogWarning("Retaining module {ModuleId} during shutdown because work or release is still active", entry.ModuleId);
                    continue;
                }
                runtime = entry.Runtime;
            }

            if (runtime is null) continue;
            try
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
                lock (_gate) { entry.Runtime = null; SetStateLocked(entry, ModuleResidencyState.Dormant); }
                ReleaseDependencies(entry);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Releasing server module {ModuleId} during shutdown failed", entry.ModuleId);
            }
        }

        _shutdown.Dispose();
        _lifetime.Dispose();
    }

    private async Task<string?> TryReleaseAsync(Entry entry, CancellationToken cancellationToken)
    {
        IServerModuleRuntime? runtime;
        lock (_gate)
        {
            if (_shutdown.IsCancellationRequested) return "module-host-stopping";
            if (entry.ResidentRequired) return "module-resident-dependent";
            if (entry.State != ModuleResidencyState.Active || entry.Leases != 0 || entry.Runtime is null) return "module-active-work";
            runtime = entry.Runtime;
            entry.ReleaseRequested = false;
            SetStateLocked(entry, ModuleResidencyState.Draining);
        }

        ModuleUnloadBlocker? blocker;
        try
        {
            blocker = await runtime.CanUnloadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate) { if (entry.State == ModuleResidencyState.Draining) SetStateLocked(entry, ModuleResidencyState.Active); }
            throw;
        }
        catch (Exception exception)
        {
            // An unreadable answer is not permission to release: keep the module and report why.
            blocker = new ModuleUnloadBlocker($"the unload check failed ({exception.GetType().Name})");
        }

        if (blocker is not null)
        {
            lock (_gate)
            {
                if (entry.State == ModuleResidencyState.Draining)
                {
                    SetStateLocked(entry, ModuleResidencyState.Active);
                    entry.Blocker = blocker.Reason;
                    entry.IdleSince = Now;
                }
            }
            return "module-release-blocked";
        }

        lock (_gate)
        {
            if (entry.State != ModuleResidencyState.Draining) return "module-active-work";
            SetStateLocked(entry, ModuleResidencyState.Unloading);
        }

        try
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Disposing the runtime of server module {ModuleId} failed", entry.ModuleId);
            lock (_gate)
            {
                // Failed disposal may leave a side-effect executor alive. Keep its identity and
                // quarantine the entry; never construct another generation over unknown residue.
                entry.Failure = "runtime release failed; host recovery required";
                entry.Blocker = "module-recovery-blocked";
                entry.RetryAfter = DateTimeOffset.MaxValue;
                SetStateLocked(entry, ModuleResidencyState.Faulted);
            }
            return "module-recovery-blocked";
        }

        ModuleLease[] releasedDependencies;
        lock (_gate)
        {
            if (ReferenceEquals(entry.Runtime, runtime)) entry.Runtime = null;
            releasedDependencies = entry.DependencyLeases;
            entry.DependencyLeases = [];
            entry.Blocker = null;
            entry.IdleSince = null;
            entry.LastReleasedAt = Now;
            SetStateLocked(entry, ModuleResidencyState.Dormant);
        }

        _logger.LogInformation("Server module {ModuleId} released after idle residency (generation {Generation})",
            entry.ModuleId, entry.Generation);
        ReleaseDependencyLeases(releasedDependencies);
        return null;
    }

    private Task<IServerModuleRuntime> StartLoadLocked(Entry entry)
    {
        if (entry.Dependencies.Length == 0 && _loading >= _options.MaxParallelLoads) throw Overloaded(entry.ModuleId);
        entry.LoadAttempts++;
        entry.Generation++;
        entry.Failure = null;
        entry.Blocker = null;
        SetStateLocked(entry, ModuleResidencyState.Loading);
        var generation = entry.Generation;
        var startedAt = _time.GetTimestamp();
        var load = LoadCoreAsync(entry, generation, startedAt);
        entry.Load = load;
        return load;
    }

    private async Task<IServerModuleRuntime> LoadCoreAsync(Entry entry, long generation, long startedAt)
    {
        List<ModuleLease> dependencies = [];
        IServerModuleRuntime? runtime = null;
        var retained = false;
        var loading = false;
        try
        {
            // Dependency initialization does not consume a factory slot for the parent. This permits
            // a dependency chain to initialize with MaxParallelLoads=1 without admission deadlock.
            foreach (var dependency in entry.Dependencies.Order(StringComparer.Ordinal))
                dependencies.Add(await AcquireCoreAsync(dependency, _shutdown.Token, dependency: true).ConfigureAwait(false));
            lock (_gate)
            {
                if (_shutdown.IsCancellationRequested) throw Stopping(entry.ModuleId);
                if (_loading >= _options.MaxParallelLoads) throw Overloaded(entry.ModuleId);
                _loading++; loading = true;
            }
            runtime = await entry.Factory.CreateAsync(_services, _moduleContext, _shutdown.Token).ConfigureAwait(false);
            if (runtime.ModuleId != entry.ModuleId) throw new InvalidOperationException("Module runtime identity mismatch.");
            double wakeMilliseconds;
            lock (_gate)
            {
                if (_shutdown.IsCancellationRequested || entry.Generation != generation) throw Stopping(entry.ModuleId);
                entry.Runtime = runtime;
                entry.DependencyLeases = dependencies.ToArray();
                retained = true;
                entry.Load = null;
                entry.LastUsedAt = Now;
                entry.IdleSince = Now;
                entry.LastWakeMilliseconds = _time.GetElapsedTime(startedAt).TotalMilliseconds;
                wakeMilliseconds = entry.LastWakeMilliseconds.Value;
                SetStateLocked(entry, ModuleResidencyState.Active);
            }
            _logger.LogInformation("Server module {ModuleId} loaded on demand in {WakeMs:0} ms (generation {Generation})",
                entry.ModuleId, wakeMilliseconds, entry.Generation);
            return runtime;
        }
        catch (Exception exception)
        {
            var cleanupFailed = retained;
            if (runtime is not null && !retained)
            {
                try { await runtime.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup)
                {
                    // A partially initialized executor may still reference dependencies. Preserve both
                    // until host recovery; never release dependencies or create a replacement executor.
                    cleanupFailed = true;
                    retained = true;
                    lock (_gate) { entry.Runtime = runtime; entry.DependencyLeases = dependencies.ToArray(); }
                    _logger.LogError(cleanup, "Module {ModuleId} initialization cleanup failed", entry.ModuleId);
                }
            }
            var stopping = _shutdown.IsCancellationRequested;
            var overloaded = exception is ModuleUnavailableException { Code: "module-wake-overloaded" };
            lock (_gate)
            {
                entry.Load = null;
                entry.Failure = cleanupFailed ? "runtime release failed; host recovery required" : $"{exception.GetType().Name}: {exception.Message}";
                entry.Blocker = cleanupFailed ? "module-recovery-blocked" : null;
                entry.RetryAfter = cleanupFailed ? DateTimeOffset.MaxValue : Now.Add(RetryDelay);
                SetStateLocked(entry, cleanupFailed ? ModuleResidencyState.Faulted : stopping || overloaded ? ModuleResidencyState.Dormant : ModuleResidencyState.Faulted);
            }
            if (stopping) throw new ModuleUnavailableException("module-host-stopping", 30,
                $"Server module {entry.ModuleId} could not load because the Host is stopping.", exception);
            if (overloaded) throw;
            _logger.LogError(exception, "Server module {ModuleId} failed to load", entry.ModuleId);
            throw new ModuleUnavailableException("module-load-failed", (int)RetryDelay.TotalSeconds,
                $"Server module {entry.ModuleId} could not be loaded: {exception.Message}", exception);
        }
        finally
        {
            if (!retained) ReleaseDependencyLeases(dependencies);
            if (loading) lock (_gate) _loading--;
        }
    }

    private static void ReleaseDependencyLeases(IEnumerable<ModuleLease> dependencies)
    {
        foreach (var lease in dependencies.Reverse()) lease.Dispose();
    }

    private void ReleaseDependencies(Entry entry)
    {
        ModuleLease[] dependencies;
        lock (_gate) { dependencies = entry.DependencyLeases; entry.DependencyLeases = []; }
        ReleaseDependencyLeases(dependencies);
    }

    private async Task SweepLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_sweepInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await SweepAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private Entry EntryFor(string moduleId) =>
        _entries.TryGetValue(moduleId, out var entry)
            ? entry
            : throw new InvalidOperationException(
                $"Server module {moduleId} does not declare a release boundary, so it has no residency entry.");

    private ModuleResidencySnapshot SnapshotLocked(Entry entry) => new(
        entry.ModuleId,
        entry.State,
        entry.Settings.IsReleasable && !entry.ResidentRequired,
        entry.Settings.Strategy,
        entry.Settings.IdleGracePeriod,
        entry.Leases,
        entry.LastUsedAt,
        entry.LastWakeMilliseconds,
        entry.Blocker ?? (entry.ResidentRequired ? "module-resident-dependent" : null),
        entry.Failure,
        entry.Generation,
        entry.LastReleasedAt);

    private void RememberSessionLocked(Entry entry)
    {
        var sessionId = CurrentSessionId();
        if (sessionId is null) return;
        if (entry.Sessions.Count >= MaxRememberedSessions) entry.Sessions.Clear();
        entry.Sessions.Add(sessionId);
    }

    private string? CurrentSessionId()
    {
        var user = _http?.HttpContext?.User;
        return user?.Identity?.IsAuthenticated == true ? user.FindFirst("sid")?.Value : null;
    }

    private static void SetStateLocked(Entry entry, ModuleResidencyState state)
    {
        entry.State = state;
        var previous = entry.StateChanged;
        entry.StateChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Waiters only receive a wake-up; they re-read the state under the same lock.
        previous.TrySetResult();
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private static ModuleUnavailableException WakeTimeout(Entry entry, Exception? inner = null) => new(
        "module-wake-timeout", 5,
        $"Server module {entry.ModuleId} did not become ready within {entry.Settings.WakeTimeout.TotalSeconds:0} seconds.",
        inner);

    private static ModuleUnavailableException Overloaded(string moduleId) => new(
        "module-wake-overloaded", 5, $"Server module {moduleId} wake admission is at capacity.");

    private static ModuleUnavailableException Stopping(string moduleId) => new(
        "module-host-stopping", 30, $"Server module {moduleId} is not available because the Host is shutting down.");

    private ModuleUnavailableException Unavailable(Entry entry) => new(
        "module-load-failed", Math.Max(1, (int)Math.Ceiling((entry.RetryAfter - Now).TotalSeconds)),
        $"Server module {entry.ModuleId} is unavailable after a failed load: {entry.Failure ?? "unknown cause"}.");

    private sealed class Entry(IServerModuleRuntimeFactory factory, ModuleResidencySettings settings)
    {
        internal int Waiting { get; set; }
        internal string[] Dependencies { get; set; } = [];
        internal ModuleLease[] DependencyLeases { get; set; } = [];
        internal bool ResidentRequired { get; set; }
        internal IServerModuleRuntimeFactory Factory { get; } = factory;
        internal ModuleResidencySettings Settings { get; set; } = settings;
        internal string ModuleId => Factory.ModuleId;
        internal ModuleResidencyState State { get; set; } = ModuleResidencyState.Dormant;
        internal IServerModuleRuntime? Runtime { get; set; }
        internal int Leases { get; set; }
        internal DateTimeOffset? IdleSince { get; set; }
        internal DateTimeOffset? LastUsedAt { get; set; }
        internal DateTimeOffset? LastReleasedAt { get; set; }
        internal double? LastWakeMilliseconds { get; set; }
        internal Task<IServerModuleRuntime>? Load { get; set; }
        internal TaskCompletionSource StateChanged { get; set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? Blocker { get; set; }
        internal string? Failure { get; set; }
        internal DateTimeOffset RetryAfter { get; set; }
        internal long Generation { get; set; }
        internal long LoadAttempts { get; set; }
        internal bool ReleaseRequested { get; set; }
        internal HashSet<string> Sessions { get; } = new(StringComparer.Ordinal);
    }
}
