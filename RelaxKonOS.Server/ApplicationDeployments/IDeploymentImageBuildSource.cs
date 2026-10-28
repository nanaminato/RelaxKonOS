using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>
/// The only fact the deployment domain needs from the Git build domain: an already completed
/// source build may be deployed as an image instead of building from an archive.
///
/// The Git module supplies the real implementation. Because that module can be disabled, the Host
/// registers <see cref="UnavailableDeploymentImageBuildSource"/> first and the module registration
/// replaces it; deployment never references the Git module's service types, never crashes on a
/// missing module, and never silently falls back to another source when the build cannot be
/// resolved. The returned operation carries the build provenance the revision record keeps.
/// </summary>
internal interface IDeploymentImageBuildSource
{
    DeploymentImageBuildResult Resolve(Guid owner, Guid buildId);
}

internal enum DeploymentImageBuildStatus
{
    /// <summary>The build succeeded and its image reference is usable.</summary>
    Available,
    /// <summary>This owner has no such build.</summary>
    NotFound,
    /// <summary>The build exists but is queued, running, cancelled or failed.</summary>
    NotReady,
    /// <summary>The Git module is not part of this process's startup snapshot.</summary>
    Unavailable,
}

internal readonly record struct DeploymentImageBuildResult(DeploymentImageBuildStatus Status, GitBuildOperationDto? Operation = null)
{
    internal static readonly DeploymentImageBuildResult Unavailable = new(DeploymentImageBuildStatus.Unavailable);
}

/// <summary>The Git module is disabled: report it instead of pretending the build exists.</summary>
internal sealed class UnavailableDeploymentImageBuildSource : IDeploymentImageBuildSource
{
    public DeploymentImageBuildResult Resolve(Guid owner, Guid buildId) => DeploymentImageBuildResult.Unavailable;
}
