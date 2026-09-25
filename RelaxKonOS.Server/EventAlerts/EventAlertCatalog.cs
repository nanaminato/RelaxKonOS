using RelaxKonOS.Protocol.EventAlerts;

namespace RelaxKonOS.Server.EventAlerts;

/// <summary>Closed source contract. Event producers select one of these definitions, never free text.</summary>
internal static class EventAlertCatalog
{
    internal sealed record Definition(string Type, OperationalEventSource Source, EventAlertSeverity DefaultSeverity,
        string ResourceType, RemediationTargetKind TargetKind, bool AllowsManualResolution);

    internal static readonly Definition DeploymentOperationFailed = new("deployment.operation_failed", OperationalEventSource.Deployment,
        EventAlertSeverity.Error, "deployment-operation", RemediationTargetKind.ApplicationDeploymentOperation, true);
    internal static readonly Definition CertificateRenewalFailed = new("certificate.renewal_failed", OperationalEventSource.Certificate,
        EventAlertSeverity.Error, "certificate", RemediationTargetKind.Certificate, false);
    internal static readonly Definition CertificateRenewalExhausted = new("certificate.renewal_exhausted", OperationalEventSource.Certificate,
        EventAlertSeverity.Critical, "certificate", RemediationTargetKind.Certificate, false);
    internal static readonly Definition CertificateExpiringSoon = new("certificate.expiring_soon", OperationalEventSource.Certificate,
        EventAlertSeverity.Warning, "certificate", RemediationTargetKind.Certificate, false);
    internal static readonly Definition GuardianAgentUnavailable = new("guardian.agent_unavailable", OperationalEventSource.Guardian,
        EventAlertSeverity.Critical, "guardian-agent", RemediationTargetKind.GuardianOverview, false);
    internal static readonly Definition GuardianWorkloadFailed = new("guardian.workload_failed", OperationalEventSource.Guardian,
        EventAlertSeverity.Error, "guardian-workload", RemediationTargetKind.GuardianWorkload, false);
    internal static readonly Definition GuardianServerRestartFailed = new("guardian.server_restart_failed", OperationalEventSource.Guardian,
        EventAlertSeverity.Critical, "guardian-server", RemediationTargetKind.GuardianOverview, false);
    internal static readonly Definition DockerEngineUnavailable = new("docker.engine_unavailable", OperationalEventSource.Docker,
        EventAlertSeverity.Error, "docker-engine", RemediationTargetKind.DockerOverview, true);
    internal static readonly Definition DockerOperationFailed = new("docker.operation_failed", OperationalEventSource.Docker,
        EventAlertSeverity.Error, "docker-operation", RemediationTargetKind.DockerOverview, true);
    internal static readonly Definition TunnelDisconnected = new("tunnel.disconnected", OperationalEventSource.Tunnel,
        EventAlertSeverity.Warning, "tunnel", RemediationTargetKind.TunnelDefinition, true);
    internal static readonly Definition SourceDegraded = new("event-center.source_degraded", OperationalEventSource.EventCenter,
        EventAlertSeverity.Critical, "event-center-source", RemediationTargetKind.EventAlertDetail, true);

    internal static IReadOnlyList<Definition> All { get; } = [
        DeploymentOperationFailed, CertificateRenewalFailed, CertificateRenewalExhausted, CertificateExpiringSoon,
        GuardianAgentUnavailable, GuardianWorkloadFailed, GuardianServerRestartFailed, DockerEngineUnavailable,
        DockerOperationFailed, TunnelDisconnected, SourceDegraded];

    internal static bool TryGet(string type, out Definition definition)
    {
        definition = All.FirstOrDefault(item => item.Type == type)!;
        return definition is not null;
    }
}
