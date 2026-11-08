using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.WebServer;

public interface INginxLifecycleExecutor
{
    Task<bool?> RunAsync(CancellationToken cancellationToken);
}

public interface INginxLifecyclePorts
{
    Guid OperationId { get; }
    Task ReportDispatchAsync(CancellationToken cancellationToken);
    Task<PrivilegedOperationResult> RunTrackedAsync();
    Task ReportConfirmedAsync();
}
