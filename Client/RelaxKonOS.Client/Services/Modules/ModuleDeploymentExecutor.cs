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
            if (release.Package.Kind != DeploymentPackageKind.ServerModule || release.Satisfied || release.ActivationOnly) continue;
            cancellationToken.ThrowIfCancellationRequested();
            await RequireServerAsync(plan.Target.ServerIdentity, cancellationToken);
            var installer = await client.PreparePackageAsync(snapshot.Store[release.ArtifactIdentity], cancellationToken);
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
        await RequireServerAsync(plan.Target.ServerIdentity, cancellationToken);
        if (release.ActivationOnly)
        {
            await EnsureEnabledAsync(plan, release, release.ArtifactIdentity, cancellationToken);
        }
        else if (!release.Satisfied)
        {
            var step = plan.PreparedSteps.Single(item => item.Package == release.Package);
            var serverPlan = await client.ReadPackagePlanAsync(step.InstallerPlanId, cancellationToken);
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
                    throw new DeploymentPlanReviewRequiredException("deployment-module-plan-expired");
                try
                {
                    var result = await client.ApplyPackagePlanAsync(serverPlan.PlanId, cancellationToken);
                    operationId = result.OperationId;
                }
                catch (InvalidOperationException error) when (error.Message == "module-package-plan-expired")
                {
                    throw new DeploymentPlanReviewRequiredException("deployment-module-plan-expired");
                }
            }

            var operation = await client.ReadPackageOperationAsync(operationId.Value, cancellationToken);
            if (operation.Action is not ("install" or "update") || operation.ModuleId != release.Package.Id
                || operation.Version != release.Version || operation.ReleaseId != serverPlan.ReleaseId
                || operation.ArtifactSha256 != serverPlan.ArtifactSha256)
                throw new InvalidOperationException("deployment-module-receipt-unavailable");
            if (operation.State == "Failed")
                throw new ModulePackageDeploymentFailedException(operation.ModuleId, operation.OperationId, operation.ProblemCode);
            if (operation.State != "Succeeded")
                throw new InvalidOperationException(operation.ProblemCode ?? "deployment-module-" + operation.State);
            await EnsureEnabledAsync(plan, release, serverPlan.ArtifactSha256, cancellationToken);
        }

        await VerifyAsync(plan, release, cancellationToken);
    }

    private async Task EnsureEnabledAsync(DeploymentPlan plan, DeploymentRelease release, string digest,
        CancellationToken cancellationToken)
    {
        var facts = await RequireServerAsync(plan.Target.ServerIdentity, cancellationToken);
        var module = facts.SingleOrDefault(item => item.Id == release.Package.Id);
        if (module is null || module.ArtifactDigest != digest)
            throw new InvalidOperationException("deployment-module-not-effective");
        if (ModuleDeploymentCatalog.RequiresRecovery(module))
            throw new ModuleDeploymentRecoveryException(module.Id, module.PendingOperationId);
        var version = release.ActivationOnly ? ModuleDeploymentCatalog.InstalledActivationVersion(module) : module.InstalledVersion;
        if (version != release.Version)
            throw new InvalidOperationException("deployment-module-not-effective");
        if (module.PendingOperationId is not null || module.RestartRequired)
            throw new InvalidOperationException("deployment-module-AwaitingRestart");
        if (module.DesiredEnabled) return;
        if (release.ActivationOnly && module.Packaging == "independent"
            && ModuleDeploymentCatalog.CatalogRevision(await client.StoreSourcesAsync(cancellationToken)) != plan.Target.CatalogRevision)
            throw new InvalidOperationException("deployment-snapshot-changed");

        var desired = facts.Where(item => item.DesiredEnabled).Select(item => item.Id)
            .Append(module.Id).Order(StringComparer.Ordinal).ToArray();
        // Preview is disposable. A retry observes the durable desired state first, then asks for
        // a fresh preview if an earlier one was blocked, expired, or never reached apply.
        var preview = await client.PreviewAsync(new(plan.Target.ServerIdentity, ServerModuleAction.Enable,
            [module.Id], desired, module.DesiredRevision!, Guid.NewGuid().ToString("N")), cancellationToken);
        if (preview.BlockedBy.Count > 0)
            throw new InvalidOperationException(preview.BlockedBy[0].ProblemCode);
        if (preview.Removed.Count != 0 || preview.Added.Count != 1 || preview.Added[0] != module.Id)
            throw new InvalidOperationException("deployment-module-plan-changed");

        cancellationToken.ThrowIfCancellationRequested();
        var operation = await client.ApplyAsync(preview.PlanId, cancellationToken);
        if (operation.State == ServerModuleOperationState.RecoveryRequired)
            throw new ModuleDeploymentRecoveryException(module.Id, operation.OperationId, operation.ProblemCode);
        if (operation.State != ServerModuleOperationState.Succeeded)
            throw new InvalidOperationException(operation.ProblemCode ?? "deployment-module-" + operation.State);
    }

    internal static string[] RequiredDesktopModules(DeploymentPlan plan, IReadOnlyList<DeploymentRequirement> rootRequirements)
    {
        var releases = plan.OrderedReleases.ToDictionary(release => release.Package);
        var required = new HashSet<DeploymentPackageKey>();
        CollectRequired(rootRequirements);
        return required.Where(package => package.Kind == DeploymentPackageKind.ServerModule)
            .Select(package => package.Id).Order(StringComparer.Ordinal).ToArray();

        void CollectRequired(IEnumerable<DeploymentRequirement> requirements)
        {
            foreach (var requirement in requirements)
                if (required.Add(requirement.Package))
                    CollectRequired(releases[requirement.Package].Requires);
        }
    }

    public async Task RegisterDesktopDependenciesAsync(DeploymentPlan plan, string[] modules, CancellationToken cancellationToken)
    {
        await RequireServerAsync(plan.Target.ServerIdentity, cancellationToken);
        await client.RegisterDesktopDependencyAsync(plan.Id,
            new(plan.Target.ServerIdentity, plan.OrderedReleases[^1].Package.Id, modules), cancellationToken);
    }

    private static bool HasPlannedCapabilities(ServerModuleDto module, DeploymentRelease release)
    {
        var observed = ModuleDeploymentCatalog.ObservedCapabilities(module);
        return release.Capabilities.All(claim => observed.TryGetValue(claim.Key, out var version)
            && (claim.Value.Length == 0 || StringComparer.Ordinal.Equals(claim.Value, version)));
    }

    public async Task VerifyAsync(DeploymentPlan plan, DeploymentRelease release, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var facts = await RequireServerAsync(plan.Target.ServerIdentity, cancellationToken);
        var module = facts.SingleOrDefault(item => item.Id == release.Package.Id);
        var identity = release.ArtifactIdentity.Split('/')[^1];
        if (module is not null && ModuleDeploymentCatalog.RequiresRecovery(module))
            throw new ModuleDeploymentRecoveryException(module.Id, module.PendingOperationId);
        if (module is null || !ModuleDeploymentCatalog.IsEffective(module))
            throw new InvalidOperationException("deployment-module-not-effective");
        if (ModuleDeploymentCatalog.EffectivePackageVersion(module) != release.Version
            || !HasPlannedCapabilities(module, release)
            || identity.Length == 64 && module.ArtifactDigest != identity)
            throw new DeploymentPlanReviewRequiredException("deployment-module-not-effective");

        if (module.Packaging == "independent")
        {
            var packages = await client.InstalledPackagesAsync(cancellationToken);
            if (!packages.Items.Any(package => package.ModuleId == module.Id && package.Active && !package.Quarantined
                && package.Version == module.EffectiveVersion && package.ArtifactSha256 == identity
                && (release.ActivationOnly
                    || $"{package.SourceId}/{package.ReleaseId}/{package.ArtifactSha256}" == release.ArtifactIdentity)))
                throw new DeploymentPlanReviewRequiredException("deployment-module-not-effective");
        }

        var health = await client.CheckHealthAsync(cancellationToken);
        if (!health.Modules.Any(item => item.ModuleId == release.Package.Id && item.Enabled
            && item.Status is "host" or "loaded" or "healthy"
            && (module.Packaging != "independent" || item.Version == module.EffectiveVersion)))
            throw new InvalidOperationException("deployment-module-unhealthy");
    }

    private async Task<IReadOnlyList<ServerModuleDto>> RequireServerAsync(string serverIdentity, CancellationToken cancellationToken)
    {
        IReadOnlyList<ServerModuleDto> facts;
        try { facts = await client.ReadAsync(cancellationToken); }
        catch (InvalidOperationException error) when (error.Message == "module-server-mismatch")
        {
            throw new DeploymentPlanReviewRequiredException("deployment-server-changed");
        }
        if (facts[0].ServiceId != serverIdentity)
            throw new DeploymentPlanReviewRequiredException("deployment-server-changed");
        return facts;
    }
}

internal sealed class ModuleDeploymentRecoveryException(string moduleId, Guid? operationId, string? problemCode = null)
    : InvalidOperationException(problemCode ?? "deployment-module-recovery-required")
{
    public string ModuleId { get; } = moduleId;
    public Guid? OperationId { get; } = operationId;
}

internal sealed class ModulePackageDeploymentFailedException(string moduleId, Guid operationId, string? problemCode)
    : DeploymentPlanReviewRequiredException(problemCode ?? "deployment-module-package-failed")
{
    public string ModuleId { get; } = moduleId;
    public Guid OperationId { get; } = operationId;
}
