namespace RelaxKonOS.Protocol.Observability;

/// <summary>Stable operational event contract. Event values are never invented at call sites.</summary>
public static class ObservabilityEventCatalog
{
    public sealed record Definition(int Id, string Name);

    public static readonly Definition RequestStarted = new(1000, "request.started");
    public static readonly Definition RequestCompleted = new(1001, "request.completed");
    public static readonly Definition RequestUnhandledException = new(1002, "request.unhandled_exception");
    public static readonly Definition LoginSucceeded = new(1100, "security.login.succeeded");
    public static readonly Definition LoginDenied = new(1101, "security.login.denied");
    public static readonly Definition TokenRejected = new(1102, "security.token.rejected");
    public static readonly Definition AuthorizationDenied = new(1200, "security.authorization.denied");
    public static readonly Definition ElevationGranted = new(1201, "security.elevation.granted");
    public static readonly Definition PrivilegedRequestAccepted = new(1300, "privileged.request.accepted");
    public static readonly Definition PrivilegedTransportRejected = new(1301, "privileged.transport.rejected");
    public static readonly Definition PrivilegedRequestCompleted = new(1302, "privileged.request.completed");
    public static readonly Definition UserExecutionRequestAccepted = new(1310, "user.execution.request.accepted");
    public static readonly Definition UserExecutionRequestCompleted = new(1311, "user.execution.request.completed");
    public static readonly Definition ConfigurationChanged = new(1400, "configuration.changed");
    public static readonly Definition ServiceRestart = new(1401, "service.restart");
    public static readonly Definition CertificateDeployed = new(1402, "certificate.deployed");
    public static readonly Definition DependencyUnavailable = new(1600, "dependency.unavailable");
    public static readonly Definition InputRejected = new(1601, "input.rejected");
    public static readonly Definition TrustValidationFailed = new(1602, "trust.validation_failed");
    public static readonly Definition StorageMigrationFailed = new(1700, "storage.migration.failed");
    public static readonly Definition OperationRecoveryStarted = new(1701, "operation.recovery.started");
    public static readonly Definition GuardianProbeFailed = new(1800, "guardian.probe.failed");
    public static readonly Definition GuardianRestartCompleted = new(1801, "guardian.restart.completed");
    public static readonly Definition SinkDegraded = new(1900, "observability.sink.degraded");
    public static readonly Definition AuditWriteFailed = new(1901, "observability.audit_write_failed");
    public static readonly Definition StorageVerified = new(1902, "observability.storage.verified");

    public static IReadOnlyList<Definition> All { get; } =
    [
        RequestStarted, RequestCompleted, RequestUnhandledException, LoginSucceeded, LoginDenied, TokenRejected,
        AuthorizationDenied, ElevationGranted, PrivilegedRequestAccepted, PrivilegedTransportRejected,
        PrivilegedRequestCompleted, ConfigurationChanged, ServiceRestart, CertificateDeployed, DependencyUnavailable,
        InputRejected, TrustValidationFailed, StorageMigrationFailed, OperationRecoveryStarted, GuardianProbeFailed,
        GuardianRestartCompleted, SinkDegraded, AuditWriteFailed, StorageVerified
    ];

    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "certificate.deploy", "configuration.change", "service.restart", "file.delete", "file.write",
        "firewall.change", "proxy.change", "tunnel.change", "docker.change", "guardian.workload.change",
        "privileged.operation", "user.execution", "authentication.login", "authorization.check"
    };

    public static bool IsKnownAction(string action) => Actions.Contains(action);
}
