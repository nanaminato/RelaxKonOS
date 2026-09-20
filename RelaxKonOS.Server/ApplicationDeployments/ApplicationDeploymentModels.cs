using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>Domain failure carrying a stable problem code. Status codes follow the existing REST convention.</summary>
/// <param name="Diagnostics">Raw output of the step that failed. It travels on the exception so the
/// coordinator — which owns the ledger and its sanitizer — can persist it, letting the service stay
/// free of any dependency on the store.</param>
/// <param name="DiagnosticsTruncated">Set when the head of that output was already dropped before it
/// got here, so the persisted record does not present a tail as the whole log.</param>
public sealed class ApplicationDeploymentException(string problemCode, int statusCode = 409,
    IReadOnlyList<string>? diagnostics = null, bool diagnosticsTruncated = false) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
    public int StatusCode { get; } = statusCode;
    public IReadOnlyList<string>? Diagnostics { get; } = diagnostics;
    public bool DiagnosticsTruncated { get; } = diagnosticsTruncated;
}

/// <param name="Progress">Verified in-stage work. Null means no reliable denominator exists, and the
/// operation must not fabricate a cumulative percentage.</param>
public sealed record ApplicationDeploymentProgress(DeploymentStage Stage, int? Progress = null, bool Cancellable = false);

public interface IApplicationDeploymentProgress
{
    Task ReportAsync(ApplicationDeploymentProgress progress, CancellationToken cancellationToken = default);
}

public sealed record ApplicationDeploymentRecovery(DeploymentOperationState State, string? ProblemCode = null, string? RecoveryProblemCode = null);

/// <summary>
/// Bounded, operator-editable deployment settings. Base images are version-line tags rather than
/// <c>latest</c>; the concrete image identity of every published revision is recorded separately.
/// </summary>
public sealed class ApplicationDeploymentOptions
{
    /// <summary>ContentRoot-relative root for the ledgers, the staged archives, the build contexts, and the
    /// materialized secret mount sources.</summary>
    public string RootDirectory { get; set; } = "data/application-deployments";
    public long MaximumArchiveBytes { get; set; } = 512L * 1024 * 1024;
    public long MaximumExpandedBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int MaximumArchiveEntries { get; set; } = 20000;
    public int MaximumPathDepth { get; set; } = 32;
    public int StagingLifetimeMinutes { get; set; } = 20;
    public int MaximumRevisionsPerApplication { get; set; } = 20;
    public int HealthCheckTimeoutSeconds { get; set; } = 180;
    public int HealthCheckIntervalSeconds { get; set; } = 2;
    public int StopTimeoutSeconds { get; set; } = 20;
    public int MaximumConcurrentOperations { get; set; } = 4;
    public double DefaultCpuCores { get; set; } = 1;
    public long DefaultMemoryBytes { get; set; } = 512L * 1024 * 1024;
    public int DefaultPidsLimit { get; set; } = 512;
    public string DefaultLogDriver { get; set; } = "json-file";
    public string DefaultLogMaxSize { get; set; } = "10m";
    public int DefaultLogMaxFiles { get; set; } = 3;
    public string JavaBaseImage { get; set; } = "eclipse-temurin:21-jre";
    public string DotNetWebBaseImage { get; set; } = "mcr.microsoft.com/dotnet/aspnet:10.0";
    public string DotNetWorkerBaseImage { get; set; } = "mcr.microsoft.com/dotnet/runtime:10.0";
    public string DotNetSelfContainedBaseImage { get; set; } = "mcr.microsoft.com/dotnet/runtime-deps:10.0";
    public string PythonBaseImage { get; set; } = "python:3.12-slim";
}

/// <summary>An operator-managed named volume. Only RelaxKonOS-managed names are ever accepted.</summary>
internal sealed record ApplicationVolumeRecord(string Name, string ContainerPath, bool ReadOnly);

/// <summary>
/// One container environment entry. A secret entry keeps only its protected-store version here; the
/// value itself never enters the catalog, a revision snapshot, the operation ledger, or an audit record.
/// </summary>
internal sealed record ApplicationConfigRecord(string Name, bool IsSecret, int SecretVersion, string? Value);

/// <summary>The mutable deployment definition of one application.</summary>
internal sealed record ApplicationRecord(
    Guid Id,
    string Name,
    string OwnerReference,
    ApplicationSourceKind SourceKind,
    ApplicationWorkloadKind WorkloadKind,
    ApplicationDesiredState DesiredState,
    ApplicationReadinessLevel ReadinessLevel,
    string? HealthCheckPath,
    int ContainerPort,
    int? HostPort,
    string BindAddress,
    ApplicationResourceLimitsDto Limits,
    ApplicationVolumeRecord[] Volumes,
    ApplicationConfigRecord[] Configuration,
    Guid? CurrentRevisionId,
    string? ContainerName,
    string? ContainerId,
    string? SiteInstanceId,
    string? SiteId,
    string? Domain,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastDeployedAt);

/// <summary>
/// An immutable published version. It binds the observed image identity, the start definition, and
/// the configuration snapshot so a rollback never has to re-derive them.
/// </summary>
internal sealed record RevisionRecord(
    Guid Id,
    Guid ApplicationId,
    int Number,
    ApplicationSourceKind SourceKind,
    string TemplateVersion,
    string InputReference,
    string ImageReference,
    string? ImageId,
    string? Platform,
    string? BaseImage,
    ApplicationWorkloadKind WorkloadKind,
    ApplicationReadinessLevel ReadinessLevel,
    string? HealthCheckPath,
    string EntryPoint,
    string[] Arguments,
    int ContainerPort,
    int? HostPort,
    string BindAddress,
    ApplicationResourceLimitsDto Limits,
    ApplicationVolumeRecord[] Volumes,
    ApplicationConfigRecord[] Configuration,
    string? SiteId,
    string CreatedByReference,
    DateTimeOffset CreatedAt);

/// <summary>
/// The validated, typed deployment input produced by a template. It is never a raw client payload:
/// every member has already been bounded and checked against the selected template.
/// </summary>
internal sealed record DeploymentPlan(
    ApplicationSourceKind SourceKind,
    string TemplateVersion,
    string ImageReference,
    string? BaseImage,
    string? ArchiveReferenceId,
    string? RuntimeVersion,
    string? ProgramEntry,
    string[] Arguments,
    bool SelfContained);

/// <summary>
/// Domain boundary for one deployment source template. A template generates a build and a start
/// definition only; it never re-implements container management.
/// </summary>
internal interface IApplicationTemplate
{
    ApplicationSourceKind Kind { get; }
    string TemplateVersion { get; }
    ApplicationDeploymentTemplateDto Describe(ApplicationDeploymentOptions options);

    /// <summary>Validates the shape of the source input before any file is touched.</summary>
    DeploymentPlan Validate(DeploymentSourceInputDto source, ApplicationRecord definition, ApplicationDeploymentOptions options, string inputReference);

    /// <summary>
    /// Inspects the extracted archive, writes the generated Dockerfile into the build context, and
    /// returns the container entry point plus its arguments.
    /// </summary>
    Task<(string EntryPoint, string[] Arguments)> PrepareBuildContextAsync(
        DeploymentPlan plan, string contextDirectory, ApplicationRecord definition, ApplicationDeploymentOptions options, CancellationToken cancellationToken);
}
