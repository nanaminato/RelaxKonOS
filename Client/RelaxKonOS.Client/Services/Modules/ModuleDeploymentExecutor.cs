using System.Collections.Immutable;
using RelaxKonOS.Deployment;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Services.Modules;

/// <summary>Prepares and reconciles signed module plans using the existing server package endpoints.</summary>
internal sealed class ModuleDeploymentExecutor(ServerModuleManagementClient client)
{
    public async Task<DeploymentPlan> PrepareAsync(DeploymentPlan plan, ModuleDeploymentSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (plan.Target != snapshot.Target)
            throw new InvalidOperationException("deployment-snapshot-changed");

        var prepared = ImmutableArray.CreateBuilder<PreparedDeploymentStep>();
        var expires = plan.ExpiresAt;
        foreach (var release in plan.OrderedReleases)
        {
            if (release.Package.Kind != DeploymentPackageKind.ServerModule || release.Satisfied) continue;
            cancellationToken.ThrowIfCancellationRequested();
            await RequireServerAsync(plan.Target.ServerIdentity);
            var installer = await client.PreparePackageAsync(snapshot.Store[release.ArtifactIdentity]);
            prepared.Add(new(release.Package, installer.PlanId, installer.ExpiresAt,
                [.. installer.Privileges.Select(permission => new DeploymentPermission(permission.Kind,
                    [.. permission.Platforms], [.. permission.Resources], permission.Executable))]));
            if (installer.ExpiresAt < expires) expires = installer.ExpiresAt;
        }

        return plan with { PreparedSteps = prepared.ToImmutable(), ExpiresAt = expires };
    }

    public async Task ApplyOrReconcileAsync(DeploymentPlan plan, DeploymentRelease release,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RequireServerAsync(plan.Target.ServerIdentity);
        if (!release.Satisfied)
        {
            var step = plan.PreparedSteps.Single(item => item.Package == release.Package);
            var serverPlan = await client.ReadPackagePlanAsync(step.InstallerPlanId);
            if (serverPlan.ModuleId != release.Package.Id || serverPlan.Version != release.Version
                || $"{serverPlan.SourceId}/{serverPlan.ReleaseId}/{serverPlan.ArtifactSha256}" != release.ArtifactIdentity
                || serverPlan.ExpiresAt != step.ExpiresAt || serverPlan.Privileges.Count != step.Permissions.Length
                || serverPlan.Privileges.Where((permission, index) =>
                    permission.Kind != step.Permissions[index].Kind
                    || permission.Executable != step.Permissions[index].Executable
                    || !permission.Platforms.SequenceEqual(step.Permissions[index].Platforms, StringComparer.Ordinal)
                    || !permission.Resources.SequenceEqual(step.Permissions[index].Resources, StringComparer.Ordinal)).Any())
                throw new InvalidOperationException("deployment-module-plan-changed");

            cancellationToken.ThrowIfCancellationRequested();
            var operationId = serverPlan.AppliedOperationId;
            if (operationId is null)
            {
                if (DateTimeOffset.UtcNow >= serverPlan.ExpiresAt)
                    throw new InvalidOperationException("deployment-module-plan-expired");
                var result = await client.ApplyPackagePlanAsync(serverPlan.PlanId);
                operationId = result.OperationId;
            }

            var operation = (await client.PackageOperationsAsync()).Items.SingleOrDefault(item => item.OperationId == operationId);
            if (operation is null || operation.ModuleId != release.Package.Id || operation.Version != release.Version
                || operation.ArtifactSha256 != serverPlan.ArtifactSha256)
                throw new InvalidOperationException("deployment-module-receipt-unavailable");
            if (operation.State != "Succeeded")
                throw new InvalidOperationException(operation.ProblemCode ?? "deployment-module-" + operation.State);
            await EnsureEnabledAsync(plan, release, serverPlan.ArtifactSha256, cancellationToken);
        }

        await VerifyAsync(plan, release, cancellationToken);
    }

    private async Task EnsureEnabledAsync(DeploymentPlan plan, DeploymentRelease release, string digest,
        CancellationToken cancellationToken)
    {
        var facts = await RequireServerAsync(plan.Target.ServerIdentity);
        var module = facts.SingleOrDefault(item => item.Id == release.Package.Id);
        if (module is null || module.InstalledVersion != release.Version || module.ArtifactDigest != digest
            || module.RecoveryState != "none")
            throw new InvalidOperationException("deployment-module-not-effective");
        if (module.PendingOperationId is not null || module.RestartRequired)
            throw new InvalidOperationException("deployment-module-AwaitingRestart");
        if (module.DesiredEnabled) return;

        var desired = facts.Where(item => item.DesiredEnabled).Select(item => item.Id)
            .Append(module.Id).Order(StringComparer.Ordinal).ToArray();
        var preview = await client.PreviewAsync(new(plan.Target.ServerIdentity, ServerModuleAction.Enable,
            [module.Id], desired, module.DesiredRevision!, $"{plan.Id:N}-enable-{plan.OrderedReleases.IndexOf(release)}"));
        if (preview.BlockedBy.Count > 0)
            throw new InvalidOperationException(preview.BlockedBy[0].ProblemCode);
        if (preview.Removed.Count != 0 || preview.Added.Count != 1 || preview.Added[0] != module.Id)
            throw new InvalidOperationException("deployment-module-plan-changed");

        cancellationToken.ThrowIfCancellationRequested();
        var operation = await client.ApplyAsync(preview.PlanId);
        if (operation.State != ServerModuleOperationState.Succeeded)
            throw new InvalidOperationException(operation.ProblemCode ?? "deployment-module-" + operation.State);
    }

    public async Task VerifyAsync(DeploymentPlan plan, DeploymentRelease release, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var facts = await RequireServerAsync(plan.Target.ServerIdentity);
        var module = facts.SingleOrDefault(item => item.Id == release.Package.Id);
        var identity = release.ArtifactIdentity.Split('/')[^1];
        if (module is not { EffectiveEnabled: true, RestartRequired: false, RecoveryState: "none" }
            || module.EffectiveVersion != release.Version
            || !release.Capabilities.Keys.All(capability => module.Capabilities.Contains(capability, StringComparer.Ordinal))
            || identity.Length == 64 && module.ArtifactDigest != identity)
            throw new InvalidOperationException("deployment-module-not-effective");

        var health = await client.CheckHealthAsync();
        if (!health.Modules.Any(item => item.ModuleId == release.Package.Id && item.Enabled
            && item.Status is "host" or "loaded" or "healthy"))
            throw new InvalidOperationException("deployment-module-unhealthy");
    }

    private async Task<IReadOnlyList<ServerModuleDto>> RequireServerAsync(string serverIdentity)
    {
        var facts = await client.ReadAsync();
        if (facts[0].ServiceId != serverIdentity)
            throw new InvalidOperationException("deployment-server-changed");
        return facts;
    }
}
