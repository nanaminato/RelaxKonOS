using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Server.Startup;

/// <summary>
/// Opt-in v2 lifecycle supervisor. It starts one isolated ModuleHost process per configured entry,
/// records ready/faulted state and stops the processes with the Server. It does not proxy HTTP or
/// grant any module access to Server services.
/// </summary>
internal sealed class ModuleHostSupervisor(IHostEnvironment environment, IConfiguration configuration,
    ILogger<ModuleHostSupervisor> logger) : BackgroundService
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];

    internal IReadOnlyList<ServerModuleHostStatusDto> Snapshot()
    {
        lock (_gate)
            return _entries.Select(entry => new ServerModuleHostStatusDto(entry.ModuleId, entry.Status,
                entry.Version, entry.HostApiVersion, entry.ProblemCode)).ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var section = configuration.GetSection("ModuleHosts");
        if (!section.GetValue<bool>("Enabled")) return;
        var entries = section.GetSection("Entries").Get<ModuleHostEntryOptions[]>() ?? [];
        foreach (var option in entries)
        {
            if (option is null || !ModulePackageHostConfiguration.Identifier(option.ModuleId)
                || string.IsNullOrWhiteSpace(option.PackageRoot) || !Path.IsPathFullyQualified(option.PackageRoot)
                || !Directory.Exists(option.PackageRoot))
            {
                logger.LogWarning("Ignoring invalid ModuleHost entry.");
                continue;
            }
            var dataPath = string.IsNullOrWhiteSpace(option.DataPath)
                ? Path.Combine(environment.ContentRootPath, "data", "module-data", option.ModuleId)
                : Path.GetFullPath(option.DataPath);
            var entry = new Entry(option.ModuleId);
            lock (_gate) _entries.Add(entry);
            ModuleHostProcessClient? client = null;
            try
            {
                client = new ModuleHostProcessClient(option.PackageRoot, option.ModuleId, dataPath);
                var ready = await client.StartAsync(stoppingToken);
                var health = await client.HealthAsync(stoppingToken);
                entry.Client = client;
                entry.Version = ready.Version;
                entry.HostApiVersion = ready.HostApiVersion;
                entry.Status = health.Healthy ? "running" : "faulted";
                entry.ProblemCode = health.ProblemCode;
                logger.LogInformation("ModuleHost {ModuleId} is {Status}.", option.ModuleId, entry.Status);
            }
            catch (Exception error) when (error is ModulePackageException or IOException or UnauthorizedAccessException)
            {
                if (client is not null) await client.DisposeAsync();
                entry.Status = "faulted";
                entry.ProblemCode = error is ModulePackageException package ? package.ProblemCode : "module-host-start-failed";
                logger.LogWarning(error, "ModuleHost {ModuleId} failed to start.", option.ModuleId);
            }
        }
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Entry[] entries;
        lock (_gate) entries = _entries.ToArray();
        foreach (var entry in entries)
            if (entry.Client is not null) await entry.Client.DisposeAsync();
    }

    private sealed class Entry(string moduleId)
    {
        internal string ModuleId { get; } = moduleId;
        internal string Status { get; set; } = "starting";
        internal string? Version { get; set; }
        internal string? HostApiVersion { get; set; }
        internal string? ProblemCode { get; set; }
        internal ModuleHostProcessClient? Client { get; set; }
    }

    private sealed record ModuleHostEntryOptions(string ModuleId, string PackageRoot, string? DataPath);
}
