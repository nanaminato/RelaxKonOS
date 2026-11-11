using System.Security.Claims;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.Startup;

/// <summary>
/// Opt-in v2 lifecycle supervisor. It starts one isolated ModuleHost process per configured entry,
/// tracks ready/health/faulted state and stops the processes with the Server.
/// Connections are exposed only while the module is healthy.
/// </summary>
internal sealed class ModuleHostSupervisor(IHostEnvironment environment, IConfiguration configuration,
    ILogger<ModuleHostSupervisor> logger, IResidentModuleWorkLeaseSource workLeases, ServerModules? modules = null, CoreModulePrivilegeBroker? broker = null, RelaxKonOS.Server.Privileged.PrivilegedHelperOptions? helperOptions = null) : BackgroundService
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private bool _stopping;

    private readonly Func<string, string, string, InstalledModuleRecord?, ModuleHostProcessClient>? _launchClient;

    internal ModuleHostSupervisor(IHostEnvironment environment, IConfiguration configuration,
        ILogger<ModuleHostSupervisor> logger, IResidentModuleWorkLeaseSource workLeases, ServerModules modules,
        Func<string, string, string, InstalledModuleRecord?, ModuleHostProcessClient> launchClient)
        : this(environment, configuration, logger, workLeases, modules) => _launchClient = launchClient;

    internal IReadOnlyList<ServerModuleHostStatusDto> Snapshot()
    {
        lock (_gate)
            return _entries.Select(entry => new ServerModuleHostStatusDto(entry.ModuleId, entry.Status,
                entry.Version, entry.HostApiVersion, entry.ProblemCode)).ToArray();
    }

    internal ModuleHostConnection? AcquireConnection(string moduleId)
    {
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(entry => entry.ModuleId == moduleId);
            if (!_stopping && entry is { Status: "running", Generation: not null } && IsTrusted(entry))
                return entry.Generation.TryAcquire();
        }
        return null;
    }

    internal ModuleHostConnection? AcquireAuthenticated(string token)
    {
        if (token is not { Length: 64 }) return null;
        lock (_gate)
            return _stopping ? null : _entries.FirstOrDefault(entry => entry.Status == "running" && entry.Generation is not null && IsTrusted(entry)
                && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(entry.Generation.ServiceToken), System.Text.Encoding.ASCII.GetBytes(token)))?.Generation?.TryAcquire();
    }

    internal bool Exists(string moduleId)
    {
        lock (_gate) return _entries.Any(entry => entry.ModuleId == moduleId);
    }

    internal bool CanAccess(string moduleId, ClaimsPrincipal user)
    {
        Entry? entry;
        lock (_gate) entry = _entries.FirstOrDefault(item => item.ModuleId == moduleId);
        if (entry is null || !IsTrusted(entry)) return false;
        if (entry.AllowedUsers.Count > 0)
        {
            var subject = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
            return subject is not null && entry.AllowedUsers.Contains(subject);
        }
        return ServerModuleManagementEndpoints.CanManage(user);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var section = configuration.GetSection("ModuleHosts");

            var probeInterval = TimeSpan.FromSeconds(Math.Clamp(section.GetValue<int?>("ProbeIntervalSeconds") ?? 10, 1, 3600));
            var entries = new List<ModuleHostEntryOptions>();
            if (section.GetValue<bool>("Enabled") && environment.IsDevelopment())
                entries.AddRange(section.GetSection("Entries").Get<ModuleHostEntryOptions[]>() ?? []);
            foreach (var record in InstalledModuleStore.Read(environment.ContentRootPath))
            {
                if (record.Runtime != "module-host" || record.Quarantined || modules?.IsEnabled(record.ModuleId) != true) continue;
                if (ModuleTrustStore.Read(environment.ContentRootPath, record)?.Trusted != true)
                {
                    logger.LogWarning("ModuleHost {ModuleId} has no exact-version code trust consent.", record.ModuleId);
                    continue;
                }
                entries.Add(new ModuleHostEntryOptions
                {
                    ModuleId = record.ModuleId, Installed = record,
                    PackageRoot = InstalledModuleStore.ResolvePackageRoot(environment.ContentRootPath, record.PackagePath),
                    AllowedUsers = section.GetSection("Access:" + record.ModuleId + ":AllowedUsers").Get<string[]>() ?? [],
                });
            }
            foreach (var option in entries)
            {
                if (option is null || !ModulePackageHostConfiguration.Identifier(option.ModuleId)
                    || string.IsNullOrWhiteSpace(option.PackageRoot) || !Path.IsPathFullyQualified(option.PackageRoot)
                    || !Directory.Exists(option.PackageRoot))
                {
                    logger.LogWarning("Ignoring invalid ModuleHost entry.");
                    continue;
                }
                if (option.Installed is null && modules?.KnownModuleIds.Contains(option.ModuleId) == true)
                {
                    logger.LogWarning("Ignoring development ModuleHost {ModuleId} that conflicts with a catalog module.", option.ModuleId);
                    continue;
                }
                var configuredUsers = option.AllowedUsers ?? [];
                if (configuredUsers.Any(user => !Guid.TryParse(user, out _)))
                {
                    logger.LogWarning("Ignoring ModuleHost {ModuleId} with an invalid AllowedUsers entry.", option.ModuleId);
                    continue;
                }
                if (Exists(option.ModuleId))
                {
                    logger.LogWarning("Ignoring duplicate ModuleHost {ModuleId}.", option.ModuleId);
                    continue;
                }
                var allowedUsers = configuredUsers.ToHashSet(StringComparer.Ordinal);
                var dataPath = string.IsNullOrWhiteSpace(option.DataPath)
                    ? Path.Combine(environment.ContentRootPath, "data", "module-data", option.ModuleId)
                    : Path.GetFullPath(option.DataPath);
                var entry = new Entry(option.ModuleId, allowedUsers, option.Installed, option.PackageRoot, dataPath);
                await entry.Lifecycle.WaitAsync(stoppingToken);
                try
                {
                    lock (_gate) { if (_stopping) break; _entries.Add(entry); }
                    await StartEntryAsync(entry, stoppingToken);
                }
                finally { entry.Lifecycle.Release(); }
            }
            using var timer = new PeriodicTimer(probeInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Entry[] active;
                lock (_gate) active = _entries.Where(entry => entry.Status == "running" && entry.Client is not null).ToArray();
                foreach (var entry in active)
                {
                    if (!entry.Lifecycle.Wait(0)) continue;
                    try
                    {
                        string? problem = null;
                        try
                        {
                            if (!IsTrusted(entry)) throw new ModulePackageException("module-host-trust-revoked");
                            using var connection = AcquireConnection(entry.ModuleId);
                            if (connection is null) continue;
                            using var probe = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, connection.Stopping);
                            var health = await entry.Client!.HealthAsync(probe.Token);
                            if (!health.Healthy) problem = health.ProblemCode ?? "module-host-unhealthy";
                        }
                        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                        {
                            continue;
                        }
                        catch (Exception error) when (error is ModulePackageException or IOException or InvalidOperationException
                            or System.Text.Json.JsonException or KeyNotFoundException)
                        {
                            problem = error is ModulePackageException package ? package.ProblemCode : "module-host-health-failed";
                        }
                        if (problem is null) continue;
                        lock (_gate)
                        {
                            entry.Status = "faulted";
                            entry.ProblemCode = problem;
                        }
                        logger.LogWarning("ModuleHost {ModuleId} faulted: {ProblemCode}.", entry.ModuleId, problem);
                        entry.Generation?.RequestClose();
                    }
                    finally { entry.Lifecycle.Release(); }
                }
            }
        }
        finally
        {
            BeginStop();
            Task[] cleanup;
            lock (_gate) cleanup = _entries.Where(entry => entry.Generation is not null).Select(entry => entry.Generation!.Completion).ToArray();
            await Task.WhenAll(cleanup);
        }
    }

    private async Task StartEntryAsync(Entry entry, CancellationToken stoppingToken, Action? beforePublish = null)
    {
        lock (_gate)
        {
            if (_stopping) throw new ModulePackageException("module-host-stopping");
            entry.Status = "starting";
            entry.ProblemCode = null;
        }
        ModuleHostGeneration? generation = null;
        IModuleWorkLease? work = null;
        try
        {
            work = await workLeases.AcquireAsync(entry.ModuleId, stoppingToken);
            if (entry.Installed is { } installed)
            {
                var consent = ModuleTrustStore.Read(environment.ContentRootPath, installed);
                if (consent?.Trusted != true) throw new ModulePackageException("module-host-trust-revoked");
                entry.TrustRevision = consent.Revision;
                VerifyLaunchPackage(installed);
            }
            var client = _launchClient?.Invoke(entry.PackageRoot, entry.ModuleId, entry.DataPath, entry.Installed)
                ?? new ModuleHostProcessClient(entry.PackageRoot, entry.ModuleId, entry.DataPath,
                logError: message => logger.LogInformation("ModuleHost {ModuleId} stderr: {Message}", entry.ModuleId, message),
                isolated: entry.Installed is not null || !environment.IsDevelopment() || configuration.GetValue<bool>("ModuleHosts:Isolation:Enabled"),
                brokerUrl: broker?.Url, helperOptions: helperOptions, installed: entry.Installed);
            generation = new(entry.ModuleId, client.ServiceToken, work, () => client.DisposeAsync().AsTask(),
                () => { lock (_gate) if (entry.Status != "faulted") entry.Status = "draining"; },
                error =>
                {
                    lock (_gate)
                    {
                        if (error is not null) { entry.Status = "faulted"; entry.ProblemCode = "module-host-cleanup-unconfirmed"; }
                        else { if (entry.Status != "faulted") entry.Status = "stopped"; entry.Generation = null; entry.Client = null; }
                    }
                });
            lock (_gate)
            {
                entry.Client = client;
                entry.Generation = generation;
                if (_stopping) generation.RequestClose();
            }
            work = null;
            generation.Start();
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, generation.Stopping);
            var ready = await client.StartAsync(startup.Token);
            var health = await client.HealthAsync(startup.Token);
            if (!IsTrusted(entry)) throw new ModulePackageException("module-host-trust-revoked");
            if (!health.Healthy)
                throw new ModulePackageException(health.ProblemCode ?? "module-host-unhealthy");
            lock (_gate)
            {
                if (_stopping || generation.Stopping.IsCancellationRequested)
                    throw new ModulePackageException("module-host-stopping");
                if (!generation.PublishReady(new Uri(ready.Url), beforePublish))
                    throw new ModulePackageException("module-host-stopping");
                entry.Version = ready.Version;
                entry.HostApiVersion = ready.HostApiVersion;
                entry.Status = "running";
                entry.ProblemCode = null;
            }
            logger.LogInformation("ModuleHost {ModuleId} is {Status}.", entry.ModuleId, entry.Status);
        }
        catch (Exception error) when (error is ModulePackageException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException or ModuleUnavailableException or ModuleManagementException)
        {
            lock (_gate)
            {
                entry.Status = "faulted";
                entry.ProblemCode = error is ModulePackageException package ? package.ProblemCode : "module-host-start-failed";
            }
            logger.LogWarning(error, "ModuleHost {ModuleId} failed to start.", entry.ModuleId);
            generation?.RequestClose();
        }
        finally { generation?.StartupFinished(); work?.Dispose(); }
    }

    internal async Task ControlAsync(string moduleId, bool restart, CancellationToken cancellationToken)
    {
        var entry = FindEntry(moduleId);
        await entry.Lifecycle.WaitAsync(cancellationToken);
        try
        {
            await DrainAndStopAsync(entry, cancellationToken);
            if (restart && entry.Installed is { } installed)
            {
                var current = InstalledModuleStore.Read(environment.ContentRootPath)
                    .SingleOrDefault(record => record.ModuleId == moduleId);
                if (current is null || current.Quarantined)
                    throw new ModulePackageException("module-host-not-available-for-update");
                SetPackage(entry, current, InstalledModuleStore.ResolvePackageRoot(environment.ContentRootPath, current.PackagePath));
            }
            if (restart)
            {
                await StartEntryAsync(entry, cancellationToken);
                EnsureRunning(entry);
            }
        }
        finally { entry.Lifecycle.Release(); }
    }

    internal async Task<string?> ReplaceAsync(InstalledModuleRecord previous, InstalledModuleRecord candidate,
        Action commit, CancellationToken cancellationToken)
    {
        var entry = FindEntry(previous.ModuleId);
        await entry.Lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (entry.Installed?.ArtifactSha256 != previous.ArtifactSha256)
                throw new ModulePackageException("module-package-plan-target-changed");

            VerifyLaunchPackage(candidate);
            var previousRoot = entry.PackageRoot;
            var candidateRoot = InstalledModuleStore.ResolvePackageRoot(environment.ContentRootPath, candidate.PackagePath);
            await DrainAndStopAsync(entry, cancellationToken);
            SetPackage(entry, candidate, candidateRoot);
            await StartEntryAsync(entry, cancellationToken, commit);
            lock (_gate)
                if (entry.Status == "running") return null;

            // StartEntryAsync requests close on failure. Require proven cleanup before restoring the old process.
            await WaitForExitAsync(entry);
            SetPackage(entry, previous, previousRoot);
            await StartEntryAsync(entry, cancellationToken);
            lock (_gate)
                if (entry.Status == "running") return "module-host-update-rolled-back";
            await WaitForExitAsync(entry);
            return "module-host-rollback-start-failed";
        }
        finally { entry.Lifecycle.Release(); }
    }

    private void VerifyLaunchPackage(InstalledModuleRecord installed)
    {
        var root = environment.ContentRootPath;
        var serviceId = ModuleManagementStore.ReadRecoveryReport(root).ServiceId;
        var catalogs = ModulePackageCatalogStore.Read(root, serviceId);
        ModulePackageCatalogStore.EnsureNotRevoked(catalogs, installed.ReleaseId, installed.ArtifactSha256);
        InstalledModuleStore.VerifyPackage(root, installed);
    }

    private Entry FindEntry(string moduleId)
    {
        lock (_gate)
        {
            if (_stopping) throw new ModulePackageException("module-host-stopping");
            return _entries.FirstOrDefault(entry => entry.ModuleId == moduleId)
                ?? throw new ModulePackageException("module-host-not-found");
        }
    }

    private void SetPackage(Entry entry, InstalledModuleRecord package, string packageRoot)
    {
        lock (_gate)
        {
            entry.Installed = package;
            entry.PackageRoot = packageRoot;
            entry.Version = null;
            entry.HostApiVersion = null;
        }
    }

    private void EnsureRunning(Entry entry)
    {
        lock (_gate)
            if (entry.Status != "running")
                throw new ModulePackageException(entry.ProblemCode ?? "module-host-start-failed");
    }

    private async Task DrainAndStopAsync(Entry entry, CancellationToken cancellationToken)
    {
        ModuleHostGeneration? generation;
        string status;
        lock (_gate)
        {
            status = entry.Status;
            generation = entry.Generation;
            if (generation is null)
            {
                entry.Status = "stopped";
                entry.ProblemCode = null;
                return;
            }
            if (status == "running") entry.Status = "draining";
        }

        if (status == "running")
        {
            try
            {
                await generation.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            catch (Exception error) when (error is TimeoutException or OperationCanceledException)
            {
                lock (_gate)
                    if (!_stopping && !generation.Stopping.IsCancellationRequested && entry.Generation == generation)
                        entry.Status = "running";
                if (error is TimeoutException) throw new ModulePackageException("module-active-work");
                throw;
            }
        }

        generation.RequestClose();
        await WaitForExitAsync(entry);
        lock (_gate)
        {
            entry.Status = "stopped";
            entry.ProblemCode = null;
        }
    }

    private async Task WaitForExitAsync(Entry entry)
    {
        Task? completion;
        lock (_gate) completion = entry.Generation?.Completion;
        if (completion is null) return;
        try { await completion.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (Exception error)
        {
            logger.LogWarning(error, "ModuleHost {ModuleId} cleanup could not be confirmed.", entry.ModuleId);
            throw new ModulePackageException("module-host-cleanup-unconfirmed");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        BeginStop();
        await base.StopAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (ExecuteTask is { } execution)
            try { await execution; } catch (OperationCanceledException) when (_stopping) { }
    }

    private void BeginStop()
    {
        ModuleHostGeneration[] generations;
        lock (_gate) { _stopping = true; generations = _entries.Where(entry => entry.Generation is not null).Select(entry => entry.Generation!).ToArray(); }
        foreach (var generation in generations) generation.RequestClose();
    }
    public override void Dispose() { BeginStop(); base.Dispose(); }

    private bool IsTrusted(Entry entry)
    {
        var installed = entry.Installed;
        if (installed is null) return environment.IsDevelopment();
        try
        {
            var consent = ModuleTrustStore.Read(environment.ContentRootPath, installed);
            return consent?.Trusted == true && consent.Revision == entry.TrustRevision;
        }
        catch (Exception error) when (error is ModulePackageException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return false; }
    }

    private sealed class Entry(string moduleId, HashSet<string> allowedUsers, InstalledModuleRecord? installed,
        string packageRoot, string dataPath)
    {
        internal string ModuleId { get; } = moduleId;
        internal InstalledModuleRecord? Installed { get; set; } = installed;
        internal string PackageRoot { get; set; } = packageRoot;
        internal string DataPath { get; } = dataPath;
        internal SemaphoreSlim Lifecycle { get; } = new(1, 1);
        internal string? TrustRevision { get; set; }
        internal HashSet<string> AllowedUsers { get; } = allowedUsers;
        internal string Status { get; set; } = "starting";
        internal string? Version { get; set; }
        internal string? HostApiVersion { get; set; }
        internal string? ProblemCode { get; set; }
        internal ModuleHostProcessClient? Client { get; set; }
        internal ModuleHostGeneration? Generation { get; set; }
    }

    private sealed class ModuleHostEntryOptions
    {
        public InstalledModuleRecord? Installed { get; set; }
        public string ModuleId { get; set; } = "";
        public string PackageRoot { get; set; } = "";
        public string? DataPath { get; set; }
        public string[]? AllowedUsers { get; set; }
    }
}
