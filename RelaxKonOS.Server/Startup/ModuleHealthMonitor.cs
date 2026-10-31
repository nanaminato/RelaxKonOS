using System.Text.Json;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Server.Startup;

/// <summary>
/// Opt-in health watchdog. It only asks the Host to stop so an external service supervisor can
/// restart it; it never changes enablement, installs, rolls back or spawns another process.
/// </summary>
internal sealed class ModuleHealthMonitor(ServerModules modules, IHostApplicationLifetime lifetime,
    IHostEnvironment environment, IConfiguration configuration, ILogger<ModuleHealthMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var section = configuration.GetSection("ModuleHealth");
        if (!section.GetValue<bool>("Enabled")) return;
        var interval = section.GetValue<int?>("ProbeIntervalSeconds") ?? 60;
        if (interval is < 10 or > 3600)
            throw new InvalidOperationException("ModuleHealth:ProbeIntervalSeconds must be between 10 and 3600.");
        var exitCode = section.GetValue<int?>("StopExitCode") ?? 1;
        if (exitCode is < 1 or > 255)
            throw new InvalidOperationException("ModuleHealth:StopExitCode must be between 1 and 255.");
        if (!section.GetValue<bool>("StopOnUnhealthy")) return;
        await WaitForStartupAsync(lifetime, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            ServerModuleHealthPageDto report;
            try { report = await modules.CheckHealthAsync(stoppingToken); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "Module health probe was unavailable.");
                continue;
            }
            var unhealthy = report.Modules.Where(ShouldStop).ToArray();
            if (unhealthy.Length == 0) continue;
            logger.LogCritical("Module health requires a Host stop: {Modules}", string.Join(", ",
                unhealthy.Select(module => module.ModuleId + ":" + module.Status + ":" + module.ProblemCode)));
            await WriteEvidenceAsync(report, exitCode, stoppingToken);
            Environment.ExitCode = exitCode;
            lifetime.StopApplication();
            return;
        }
    }

    internal static bool ShouldStop(ServerModuleHealthModuleDto module) => module.Enabled
        && module.Status is "unhealthy" or "load-failed" or "quarantined";

    private static async Task WaitForStartupAsync(IHostApplicationLifetime lifetime, CancellationToken cancellationToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(cancellationToken);
    }

    private async Task WriteEvidenceAsync(ServerModuleHealthPageDto report, int exitCode, CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.Combine(environment.ContentRootPath, "data", "module-management", "health-stop.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                stoppedAt = DateTimeOffset.UtcNow,
                exitCode,
                report.Healthy,
                report.CheckedAt,
                report.Modules,
            }, RelaxKonOSJsonOptions.Default), cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Could not persist module health stop evidence.");
        }
    }
}
