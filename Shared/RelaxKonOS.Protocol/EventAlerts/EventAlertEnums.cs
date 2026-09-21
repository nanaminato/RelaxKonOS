namespace RelaxKonOS.Protocol.EventAlerts;

/// <summary>Closed severity vocabulary for the Event & Alert Center.</summary>
public enum EventAlertSeverity { Warning, Error, Critical }

/// <summary>Lifecycle of an aggregated operational alert.</summary>
public enum OperationalAlertStatus { Open, Acknowledged, Resolved, Suppressed }

/// <summary>Trusted producer categories. Producers must not invent values at runtime.</summary>
public enum OperationalEventSource { Deployment, Certificate, Guardian, Docker, Tunnel, EventCenter }

/// <summary>Whether a durable source observation represents an active condition or verified recovery.</summary>
public enum OperationalEventOutcome { Failed, Recovered }

/// <summary>Fixed client-side destinations. The server never sends a URI or command.</summary>
public enum RemediationTargetKind
{
    None,
    ApplicationDeploymentOperation,
    Certificate,
    GuardianOverview,
    GuardianWorkload,
    DockerOverview,
    TunnelDefinition,
    EventAlertDetail,
}
