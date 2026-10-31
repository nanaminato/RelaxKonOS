using System.Security.Claims;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Endpoints;

namespace RelaxKonOS.Server.Startup;

/// <summary>
/// Opt-in v2 lifecycle supervisor. It starts one isolated ModuleHost process per configured entry,
/// tracks ready/health/faulted state and stops the processes with the Server.
/// Connections are exposed only while the module is healthy.
/// </summary>
internal sealed class ModuleHostSupervisor(IHostEnvironment environment, IConfiguration configuration,
    ILogger<ModuleHostSupervisor> logger, ServerModules? modules = null, CoreModulePrivilegeBroker? broker = null, RelaxKonOS.Server.Privileged.PrivilegedHelperOptions? helperOptions = null) : BackgroundService
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];

    internal IReadOnlyList<ServerModuleHostStatusDto> Snapshot()
    {
        lock (_gate)
            return _entries.Select(entry => new ServerModuleHostStatusDto(entry.ModuleId, entry.Status,
                entry.Version, entry.HostApiVersion, entry.ProblemCode)).ToArray();
    }

    internal bool TryGetEndpoint(string moduleId, out Uri endpoint) => TryGetConnection(moduleId, out endpoint, out _);

    internal bool TryGetConnection(string moduleId, out Uri endpoint, out string serviceToken)
    {
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(entry => entry.ModuleId == moduleId);
            if (entry is { Status: "running", Client: not null } && IsTrusted(entry)
                && Uri.TryCreate(entry.Client.Ready.Url, UriKind.Absolute, out var parsed)
                && parsed.Scheme == "http" && parsed.IsLoopback)
            {
                endpoint = parsed;
                serviceToken = entry.Client.ServiceToken;
                return true;
            }
        }
        endpoint = null!;
        serviceToken = "";
        return false;
    }

    internal string? Authenticate(string token)
    {
        if (token is not { Length: 64 }) return null;
        lock (_gate)
            return _entries.FirstOrDefault(entry => entry.Status == "running" && entry.Client is not null
                && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(entry.Client.ServiceToken), System.Text.Encoding.ASCII.GetBytes(token)))?.ModuleId;
    }

    internal bool Exists(string moduleId)
    {
        lock (_gate) return _entries.Any(entry => entry.ModuleId == moduleId);
    }

    internal bool CanAccess(string moduleId, ClaimsPrincipal user, IConfiguration configuration)
    {
        Entry? entry;
        lock (_gate) entry = _entries.FirstOrDefault(item => item.ModuleId == moduleId);
        if (entry is null || !IsTrusted(entry)) return false;
        if (entry.AllowedUsers.Count > 0)
        {
            var subject = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
            return subject is not null && entry.AllowedUsers.Contains(subject);
        }
        return ServerModuleManagementEndpoints.CanManage(user, configuration);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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
            InstalledModuleStore.VerifyPackage(environment.ContentRootPath, record);
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
            var entry = new Entry(option.ModuleId, allowedUsers, option.Installed);
            lock (_gate) _entries.Add(entry);
            ModuleHostProcessClient? client = null;
            try
            {
                client = new ModuleHostProcessClient(option.PackageRoot, option.ModuleId, dataPath,
                    logError: message => logger.LogInformation("ModuleHost {ModuleId} stderr: {Message}", option.ModuleId, message),
                    isolated: !environment.IsDevelopment() || section.GetValue<bool>("Isolation:Enabled"), brokerUrl: broker?.Url, helperOptions: helperOptions);
                var ready = await client.StartAsync(stoppingToken);
                var health = await client.HealthAsync(stoppingToken);
                lock (_gate)
                {
                    entry.Client = client;
                    entry.Version = ready.Version;
                    entry.HostApiVersion = ready.HostApiVersion;
                    entry.Status = health.Healthy ? "running" : "faulted";
                    entry.ProblemCode = health.Healthy ? null : health.ProblemCode ?? "module-host-unhealthy";
                }
                if (!health.Healthy)
                {
                    await client.DisposeAsync();
                    lock (_gate) entry.Client = null;
                }
                logger.LogInformation("ModuleHost {ModuleId} is {Status}.", option.ModuleId, entry.Status);
            }
            catch (Exception error) when (error is ModulePackageException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
            {
                if (client is not null) await client.DisposeAsync();
                lock (_gate)
                {
                    entry.Status = "faulted";
                    entry.ProblemCode = error is ModulePackageException package ? package.ProblemCode : "module-host-start-failed";
                }
                logger.LogWarning(error, "ModuleHost {ModuleId} failed to start.", option.ModuleId);
            }
        }
        using var timer = new PeriodicTimer(probeInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            Entry[] active;
            lock (_gate) active = _entries.Where(entry => entry.Status == "running" && entry.Client is not null).ToArray();
            foreach (var entry in active)
            {
                string? problem = null;
                try
                {
                    if (!IsTrusted(entry)) throw new ModulePackageException("module-host-trust-revoked");
                    var health = await entry.Client!.HealthAsync(stoppingToken);
                    if (!health.Healthy) problem = health.ProblemCode ?? "module-host-unhealthy";
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
                await entry.Client!.AbortAsync();
                await entry.Client.DisposeAsync();
                lock (_gate) entry.Client = null;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Entry[] entries;
        lock (_gate) entries = _entries.ToArray();
        foreach (var entry in entries)
            if (entry.Client is not null) await entry.Client.DisposeAsync();
    }

    private bool IsTrusted(Entry entry)
    {
        if (entry.Installed is null) return environment.IsDevelopment();
        try { return ModuleTrustStore.Read(environment.ContentRootPath, entry.Installed)?.Trusted == true; }
        catch (Exception error) when (error is ModulePackageException or IOException or UnauthorizedAccessException) { return false; }
    }

    private sealed class Entry(string moduleId, HashSet<string> allowedUsers, InstalledModuleRecord? installed)
    {
        internal string ModuleId { get; } = moduleId;
        internal InstalledModuleRecord? Installed { get; } = installed;
        internal HashSet<string> AllowedUsers { get; } = allowedUsers;
        internal string Status { get; set; } = "starting";
        internal string? Version { get; set; }
        internal string? HostApiVersion { get; set; }
        internal string? ProblemCode { get; set; }
        internal ModuleHostProcessClient? Client { get; set; }
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
