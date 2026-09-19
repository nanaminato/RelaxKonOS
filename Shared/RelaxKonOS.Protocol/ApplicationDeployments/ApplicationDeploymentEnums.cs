using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.ApplicationDeployments;

/// <summary>The four first-stage deployment templates. Each generates a build and a start definition
/// instead of copying container management into the template itself.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ApplicationSourceKind>))]
public enum ApplicationSourceKind { Image, JavaJar, DotNetPublish, PythonProject }

/// <summary>The operator's intent. The observed <see cref="ApplicationActualState"/> is separate so a
/// failed deployment can never be reported as running.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ApplicationDesiredState>))]
public enum ApplicationDesiredState { Stopped, Running }

/// <summary>Observed workload state, reconciled against real containers rather than the ledger alone.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ApplicationActualState>))]
public enum ApplicationActualState { Unknown, Missing, Stopped, Starting, Running, Stopping, Failed }

/// <summary>
/// Readiness depth for a workload. <see cref="Http"/> probes a loopback HTTP endpoint;
/// <see cref="Process"/> is the explicitly weaker process-alive level a worker workload must select.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ApplicationReadinessLevel>))]
public enum ApplicationReadinessLevel { Process, Http }

/// <summary>Distinguishes a served workload from a background worker; it selects the .NET base image.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ApplicationWorkloadKind>))]
public enum ApplicationWorkloadKind { Web, Worker }

[JsonConverter(typeof(JsonStringEnumConverter<DeploymentOperationKind>))]
public enum DeploymentOperationKind { Deploy, Rollback, Start, Stop, Restart, Delete }

[JsonConverter(typeof(JsonStringEnumConverter<DeploymentOperationState>))]
public enum DeploymentOperationState { Queued, Running, Succeeded, Failed, Cancelled, Interrupted }

/// <summary>
/// Operation stages. <see cref="Pulling"/> and <see cref="Building"/> are distinct so progress never
/// has to guess whether bytes are arriving from a registry or a local build.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeploymentStage>))]
public enum DeploymentStage
{
    Queued,
    Preflight,
    Preparing,
    Pulling,
    Building,
    Creating,
    HealthChecking,
    Activating,
    Deactivating,
    CleaningUp,
    RollingBack,
    Completed,
    Failed,
    Cancelled,
    Interrupted
}
