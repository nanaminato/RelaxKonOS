using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace RelaxKonOS.Deployment;

public sealed record DeploymentTarget(string ServerIdentity, string StateRevision, string CatalogRevision);

public sealed record DeploymentPermission(string Kind, ImmutableArray<string> Platforms,
    ImmutableArray<string> Resources, bool Executable);

public sealed record PreparedDeploymentStep(DeploymentPackageKey Package, Guid InstallerPlanId,
    DateTimeOffset ExpiresAt, ImmutableArray<DeploymentPermission> Permissions);

public sealed record DeploymentClientBinding(Guid AccountId, string ConnectionIdentity, string? InstalledPath)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArchivePath { get; init; }
}

public sealed record DeploymentPlan(Guid Id, DeploymentTarget Target, DateTimeOffset ExpiresAt,
    ImmutableArray<DeploymentRelease> OrderedReleases)
{
    public ImmutableArray<PreparedDeploymentStep> PreparedSteps { get; init; } = [];
    public DeploymentClientBinding? ClientBinding { get; init; }
}

public sealed record DeploymentCheckpoint(int CompletedSteps, bool Verified);

/// <summary>A backend must use the plan ID and step index to reconcile retries with durable installer receipts.</summary>
public interface IDeploymentBackend
{
    Task<DeploymentTarget> ObserveAsync(CancellationToken cancellationToken);
    Task ApplyOrReconcileAsync(DeploymentPlan plan, int stepIndex, CancellationToken cancellationToken);
    Task VerifyAsync(DeploymentPlan plan, CancellationToken cancellationToken);
}

/// <summary>The lease serializes execution of a plan across processes and binds its immutable contents.</summary>
public interface IDeploymentJournal
{
    Task<IDeploymentLease> OpenAsync(DeploymentPlan plan, CancellationToken cancellationToken);
}

public interface IDeploymentLease : IAsyncDisposable
{
    DeploymentCheckpoint? Checkpoint { get; }
    Task SaveAsync(DeploymentCheckpoint checkpoint, CancellationToken cancellationToken);
}

public sealed class UnifiedDeploymentCoordinator(IDeploymentBackend backend, IDeploymentJournal journal,
    TimeProvider clock)
{
    public async Task<DeploymentPlan> PlanAsync(DeploymentTarget snapshot, DeploymentRelease root,
        IEnumerable<DeploymentRelease> candidates, IEnumerable<DeploymentRequirement> selectedOptional,
        CancellationToken cancellationToken = default)
    {
        var resolution = DeploymentResolver.Resolve(root, candidates, selectedOptional);
        if (resolution.Problem is { } problem)
            throw new DeploymentResolutionException(problem, resolution.ProblemPackage);
        if (snapshot != await backend.ObserveAsync(cancellationToken))
            throw new InvalidOperationException("deployment-snapshot-changed");

        return new(Guid.NewGuid(), snapshot, clock.GetUtcNow().AddMinutes(10), resolution.OrderedReleases);
    }

    public async Task ExecuteAsync(DeploymentPlan plan, CancellationToken cancellationToken = default)
    {
        await using var lease = await journal.OpenAsync(plan, cancellationToken);
        var checkpoint = lease.Checkpoint;
        var current = await backend.ObserveAsync(cancellationToken);
        if (current.ServerIdentity != plan.Target.ServerIdentity)
            throw new InvalidOperationException("deployment-server-changed");

        if (checkpoint is null)
        {
            if (clock.GetUtcNow() >= plan.ExpiresAt)
                throw new InvalidOperationException("deployment-plan-expired");
            if (current != plan.Target)
                throw new InvalidOperationException("deployment-snapshot-changed");

            checkpoint = new(0, false);
            await lease.SaveAsync(checkpoint, cancellationToken);
        }

        if (checkpoint.CompletedSteps < 0 || checkpoint.CompletedSteps > plan.OrderedReleases.Length
            || checkpoint.Verified && checkpoint.CompletedSteps != plan.OrderedReleases.Length)
            throw new InvalidDataException("Invalid deployment checkpoint.");
        if (checkpoint.Verified) return;

        for (var index = checkpoint.CompletedSteps; index < plan.OrderedReleases.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await backend.ApplyOrReconcileAsync(plan, index, cancellationToken);
            // A completed backend step must be recorded before honoring cancellation.
            await lease.SaveAsync(new(index + 1, false), CancellationToken.None);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await backend.VerifyAsync(plan, cancellationToken);
        await lease.SaveAsync(new(plan.OrderedReleases.Length, true), CancellationToken.None);
    }
}
