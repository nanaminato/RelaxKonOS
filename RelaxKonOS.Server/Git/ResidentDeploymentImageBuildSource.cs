using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Git;

/// <summary>
/// Deployment's view of the Git build ledger. The deployment domain is not a user request, so it takes
/// a short synchronous lease: a dormant Git module is loaded on demand, and any failure (module absent,
/// load failure or wake timeout) answers "unavailable" instead of the misleading "not found".
/// </summary>
internal sealed class ResidentDeploymentImageBuildSource(ModuleResidencyCoordinator residency) : IDeploymentImageBuildSource
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(5);

    public DeploymentImageBuildResult Resolve(Guid owner, Guid buildId)
    {
        using var lease = residency.TryAcquire(ServerModules.GitId, LoadTimeout);
        if (lease is null) return new DeploymentImageBuildResult(DeploymentImageBuildStatus.Unavailable);
        var builds = (IGitBuildService)lease.Runtime.Resolve(typeof(IGitBuildService));
        return new GitDeploymentImageBuildSource(builds).Resolve(owner, buildId);
    }
}
