using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.Modules;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Deployment;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Runtime;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public sealed partial class ApplicationAvailabilityService
{
    private IReadOnlyList<DeploymentRequirement> InstalledRequiredDependencies(AppId id) =>
        services.GetService<DeveloperPackageManager>()?.RequiredDependencies(id) ?? [];

    private ApplicationAvailability? ReadOfflinePackageAvailability(AppId id)
    {
        var required = InstalledRequiredDependencies(id);
        if (required.Count == 0 || ComponentCatalog.LaunchGate(id.Value) is not null) return null;
        var root = PackageRoot(id, required);
        var result = DeploymentResolver.Resolve(root, ReadInstalledApplications(root.Package), []);
        var affected = result.ProblemPackage ?? required[0].Package;
        var state = result.Succeeded ? ComponentAvailabilityKind.Installed
            : affected.Kind == DeploymentPackageKind.ServerModule ? ComponentAvailabilityKind.Unknown
            : ComponentAvailabilityKind.Missing;
        return new(id, null, affected.Id, state, PackageKind: affected.Kind);
    }

    private async Task<ApplicationAvailability?> ObservePackageDependenciesAsync(AppId id,
        IReadOnlyList<DeploymentRequirement> required)
    {
        var root = PackageRoot(id, required);
        var rootKey = root.Package;
        var login = session.CurrentSession;
        var user = session.CurrentUser?.Id;
        var server = session.ServiceId;
        long generation;
        lock (_cacheLock) generation = _generation;

        var local = ReadInstalledApplications(rootKey).ToArray();
        var result = DeploymentResolver.Resolve(root, local, []);
        var usedServer = false;
        ComponentAvailabilityKind? serverFailure = null;

        if (result.ProblemPackage is { Kind: DeploymentPackageKind.ServerModule } missing)
        {
            if (login is null || server is null || user is null || session.State != AuthSessionState.Authenticated)
                return new(id, server, missing.Id, ComponentAvailabilityKind.Unknown, PackageKind: missing.Kind);
            var http = services.GetRequiredService<IHttpClientFactory>();
            var client = new ServerModuleManagementClient(http.CreateClient(ServerModuleCatalog.HttpClientName), session);
            if (!HasOriginalLogin()) return null;
            usedServer = true;
            try
            {
                var modules = await client.ReadAsync();
                if (!client.IsCurrent || !HasOriginalLogin()) return null;
                local = ReadInstalledApplications(rootKey).ToArray();
                result = DeploymentResolver.Resolve(root, local.Concat(modules
                    .Where(ModuleDeploymentCatalog.IsEffective)
                    .Select(module => (Module: module, Version: ModuleDeploymentCatalog.EffectivePackageVersion(module)))
                    .Where(item => item.Version is not null)
                    .Select(item => new DeploymentRelease(
                        new(DeploymentPackageKind.ServerModule, item.Module.Id), item.Version!, item.Module.ArtifactDigest ?? "",
                        true, [.. item.Module.Requires.Select(dependency => new DeploymentRequirement(new(DeploymentPackageKind.ServerModule, dependency)))],
                        item.Module.Capabilities.Distinct(StringComparer.Ordinal)
                            .ToImmutableDictionary(capability => capability, _ => "", StringComparer.Ordinal)))), []);
                if (result.ProblemPackage is { Kind: DeploymentPackageKind.ServerModule } unavailable
                    && modules.SingleOrDefault(module => module.Id == unavailable.Id) is { } dependency)
                {
                    if (ModuleDeploymentCatalog.RequiresRecovery(dependency))
                        serverFailure = ComponentAvailabilityKind.Recovery;
                    else if (dependency.PendingOperationId is not null || dependency.RestartRequired)
                        serverFailure = ComponentAvailabilityKind.Busy;
                    else if (dependency is { Packaging: "independent", EffectiveEnabled: true, EffectiveVersion: null })
                        serverFailure = ComponentAvailabilityKind.Unknown;
                }
            }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or HttpRequestException
                or OperationCanceledException or System.Text.Json.JsonException)
            {
                if (!client.IsCurrent || !HasOriginalLogin()) return null;
                serverFailure = error.Message is "module-http-401" or "module-http-403"
                    ? ComponentAvailabilityKind.NoPermission : ComponentAvailabilityKind.Unknown;
            }
        }

        if (!required.SequenceEqual(InstalledRequiredDependencies(id))) return null;
        var affected = result.ProblemPackage ?? required[0].Package;
        var availability = new ApplicationAvailability(id, usedServer ? server : null, affected.Id,
            serverFailure ?? (result.Succeeded ? ComponentAvailabilityKind.Installed : ComponentAvailabilityKind.Missing),
            PackageKind: affected.Kind);

        if (server is null || user is null) return availability;
        var key = (server, user.Value, id.Value);
        bool changed;
        lock (_cacheLock)
        {
            if (generation != _generation) return null;
            if (availability.CanOpen && ComponentCatalog.LaunchGate(id.Value) is not null)
                return availability;
            changed = !_observations.TryGetValue(key, out var previous) || previous != availability;
            _observations[key] = availability;
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return availability;

        bool HasOriginalLogin() => session.State == AuthSessionState.Authenticated
            && ReferenceEquals(login, session.CurrentSession) && server == session.ServiceId && user == session.CurrentUser?.Id;
    }

    private static DeploymentRelease PackageRoot(AppId id, IReadOnlyList<DeploymentRequirement> required) =>
        new(new(DeploymentPackageKind.Application, id.Value), "0.0.0", "", true,
            [.. required], ImmutableDictionary<string, string>.Empty);

    private IEnumerable<DeploymentRelease> ReadInstalledApplications(DeploymentPackageKey root)
    {
        var applications = services.GetService<ApplicationManager>();
        var installed = services.GetService<DeveloperPackageManager>()?.InstalledRecords ?? [];
        return installed.Where(record => record.Id != root.Id
                && RuntimeSemanticVersion.TryParse(record.Version, out _)
                && applications?.GetManifest(new AppId(record.Id))?.Version == record.Version)
            .Select(record => new DeploymentRelease(new(DeploymentPackageKind.Application, record.Id), record.Version,
                record.Path, true, [.. record.Dependencies?.Required ?? []], ImmutableDictionary<string, string>.Empty));
    }
}
