using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.WebServer;

public interface INginxReloadExecutor
{
    Task<string> ReloadAsync(CancellationToken cancellationToken);
}

public interface INginxReloadPorts
{
    Guid OperationId { get; }
    bool UsesSystemPackage { get; }
    Task ReportDispatchAsync(CancellationToken cancellationToken);
    Task ReportResponseAsync(bool confirmed);
    Task<PrivilegedOperationResult> RunTrackedAsync(Guid operationId, CancellationToken cancellationToken);
    Task<bool> RunDirectAsync(CancellationToken cancellationToken);
    string ToReloadProblem(PrivilegedProblemCode problem);
}

public static class NginxReloadOutcome
{
    public const string RecoveryRequired = "webserver.native_recovery_required";
}
