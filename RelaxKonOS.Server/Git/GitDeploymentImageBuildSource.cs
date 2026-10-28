using RelaxKonOS.Server.ApplicationDeployments;

namespace RelaxKonOS.Server.Git;

/// <summary>
/// Adapts the Git build ledger to the deployment port. The Git module owns this registration, so a
/// disabled module leaves <see cref="UnavailableDeploymentImageBuildSource"/> in place and the
/// deployment domain reports "git build unavailable" instead of a misleading "image not found".
/// </summary>
internal sealed class GitDeploymentImageBuildSource(GitBuildService builds) : IDeploymentImageBuildSource
{
    public DeploymentImageBuildResult Resolve(Guid owner, Guid buildId)
    {
        try
        {
            var operation = builds.Artifact(owner, buildId);
            return new(DeploymentImageBuildStatus.Available, operation);
        }
        catch (GitBuildProblem problem) when (problem.Code == "git-build.not-found")
        {
            return new(DeploymentImageBuildStatus.NotFound);
        }
        catch (GitBuildProblem)
        {
            // Queued/running/cancelled/failed builds and an unavailable ledger share one answer for
            // the caller: there is no deployable artifact yet. The build id itself is still in the
            // ledger for the Git application to explain.
            return new(DeploymentImageBuildStatus.NotReady);
        }
    }
}
