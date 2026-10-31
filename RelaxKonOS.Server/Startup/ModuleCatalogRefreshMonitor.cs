using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.Server.Startup;

/// <summary>
/// Opt-in periodic refresh of already accepted module catalogs. It only fetches configured HTTPS
/// URIs and commits a catalog that passes the same signature/policy/history checks as an explicit
/// refresh; it never installs, enables, loads or restarts a module.
/// </summary>
internal sealed class ModuleCatalogRefreshMonitor(IHostApplicationLifetime lifetime,
    IHostEnvironment environment, IConfiguration configuration, ILogger<ModuleCatalogRefreshMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var section = configuration.GetSection("ModulePackages:Refresh");
        if (!section.GetValue<bool>("Enabled")) return;
        var interval = section.GetValue<int?>("IntervalSeconds") ?? 3600;
        if (interval is < 60 or > 86400)
            throw new InvalidOperationException("ModulePackages:Refresh:IntervalSeconds must be between 60 and 86400.");
        await WaitForStartupAsync(lifetime, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
        do
        {
            try { await RefreshOnceAsync(stoppingToken); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "Periodic module catalog refresh failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        var root = environment.ContentRootPath;
        var serviceId = ModuleManagementStore.ReadRecoveryReport(root).ServiceId;
        var context = ModulePackageHostConfiguration.Context(serviceId, configuration, environment);
        var revision = ModulePackageCatalogStore.Read(root, serviceId).Revision;
        foreach (var sourceId in ModulePackageCatalogStore.Read(root, serviceId).Sources.Keys.Order(StringComparer.Ordinal))
        {
            var uri = configuration["ModulePackages:Sources:" + sourceId + ":CatalogUri"];
            if (string.IsNullOrWhiteSpace(uri)) continue;
            try
            {
                var envelope = await ModulePackageCatalogFetcher.FetchAsync(uri, cancellationToken: cancellationToken);
                var policy = ModulePackageHostConfiguration.Source(configuration, sourceId);
                using var store = new ModulePackageCatalogStore(root, serviceId, takeHostLock: false);
                var receipt = store.Accept(envelope, policy, context, revision, "auto-refresh",
                    "auto-" + sourceId + "-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), DateTimeOffset.UtcNow);
                revision = receipt.Revision;
                logger.LogInformation("Refreshed module catalog {SourceId} to sequence {Sequence}.", sourceId,
                    receipt.Sequence);
            }
            catch (ModulePackageException error) when (error.ProblemCode == "module-package-catalog-replay")
            {
                // The source is already at this sequence; this is the normal no-op path.
            }
            catch (Exception error) when (error is ModulePackageException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(error, "Automatic module catalog refresh for {SourceId} was skipped.", sourceId);
            }
        }
    }

    private static async Task WaitForStartupAsync(IHostApplicationLifetime lifetime, CancellationToken cancellationToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(cancellationToken);
    }
}
