using Microsoft.Extensions.Hosting;
using RelaxKonOS.Protocol.Observability;

namespace RelaxKonOS.Server.Observability;

/// <summary>Fails a production start before accepting work when managed observability storage is unusable.</summary>
public sealed class ObservabilityStartupValidationService(IHostEnvironment environment, ObservabilityOptions options,
    ISecurityAuditWriter audits) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsProduction()) return;
        var probe = Path.Combine(options.LogDirectory!, $".relaxkonos-write-probe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(options.LogDirectory!);
            await using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 1, FileOptions.WriteThrough | FileOptions.Asynchronous))
                await stream.FlushAsync(cancellationToken);
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The managed runtime-log directory is not writable by this service.", exception);
        }

        var written = await audits.TryWriteAsync(new SecurityAuditEvent(
            ObservabilityEventCatalog.StorageVerified.Id, ObservabilityEventCatalog.StorageVerified.Name,
            ObservabilityOutcome.Succeeded, "server", Guid.NewGuid(), DateTimeOffset.UtcNow, options.InstanceId!,
            Action: "configuration.change", ProblemCode: "observability.startup_verified"), cancellationToken);
        if (!written) throw new InvalidOperationException("The managed security-audit store is not writable by this service.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
