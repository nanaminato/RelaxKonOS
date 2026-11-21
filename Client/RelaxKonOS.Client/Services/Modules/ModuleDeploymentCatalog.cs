using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.Deployment;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Services.Modules;

internal sealed record ModuleDeploymentSnapshot(DeploymentTarget Target, ImmutableArray<DeploymentRelease> Releases,
    ImmutableDictionary<string, ServerModuleStoreItemDto> Store);

/// <summary>Projects module management facts and signed catalog metadata into the deployment planner.</summary>
internal sealed class ModuleDeploymentCatalog(ServerModuleManagementClient client)
{
    public async Task<ModuleDeploymentSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var sources = await client.StoreSourcesAsync();
        var store = ImmutableDictionary.CreateBuilder<string, ServerModuleStoreItemDto>(StringComparer.Ordinal);
        for (int? offset = 0; offset is { } position;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position > 2048) throw new InvalidOperationException("deployment-module-catalog-limit");
            var page = await client.SearchStoreAsync(null, offset: position);
            foreach (var item in page.Items)
            {
                if (!item.RidCompatible || !item.HostApiCompatible || item.Revoked || item.BlockedBy.Count != 0)
                    continue;
                if (!store.TryAdd(Identity(item.SourceId, item.ReleaseId, item.ArtifactSha256), item))
                    throw new InvalidDataException("Duplicate module release in deployment catalog.");
            }
            offset = page.NextOffset;
        }

        var modules = await client.ReadAsync();
        var installed = (await client.InstalledPackagesAsync()).Items;
        var verifiedModules = await client.ReadAsync();
        var verifiedSources = await client.StoreSourcesAsync();
        var target = Target(modules, sources);
        if (target != Target(verifiedModules, verifiedSources))
            throw new InvalidOperationException("deployment-snapshot-changed");

        var releases = ImmutableArray.CreateBuilder<DeploymentRelease>();
        foreach (var module in modules)
        {
            var package = installed.SingleOrDefault(item => item.ModuleId == module.Id);
            var effectiveVersion = EffectivePackageVersion(module);
            if (!module.EffectiveEnabled || module.RecoveryState != "none"
                || module.RestartRequired || effectiveVersion is null)
                continue;
            var identity = package is { Active: true, Quarantined: false } && package.Version == module.EffectiveVersion
                ? Identity(package.SourceId, package.ReleaseId, package.ArtifactSha256)
                : module.ArtifactDigest ?? $"bundled/{module.Id}/{module.EffectiveVersion}";
            var dependencies = store.TryGetValue(identity, out var signedRelease)
                ? signedRelease.Dependencies.Select(ToRequirement)
                : module.Requires.Select(id => new DeploymentRequirement(Key(id)));
            releases.Add(new(Key(module.Id), effectiveVersion, identity, true, [..dependencies],
                Capabilities(module.Capabilities)));
        }

        foreach (var entry in store)
        {
            var release = entry.Value;
            releases.Add(new(Key(release.ModuleId), release.Version, entry.Key, false,
                [..release.Dependencies.Select(ToRequirement)], Capabilities(release.Capabilities))
            {
                CurrentVersion = modules.SingleOrDefault(item => item.Id == release.ModuleId)?.EffectiveVersion
                    ?? installed.SingleOrDefault(item => item.ModuleId == release.ModuleId)?.Version,
            });
        }

        return new(target, releases.ToImmutable(), store.ToImmutable());
    }

    public async Task<DeploymentTarget> ObserveAsync()
    {
        var modules = await client.ReadAsync();
        var sources = await client.StoreSourcesAsync();
        return Target(modules, sources);
    }

    private static DeploymentTarget Target(IReadOnlyList<ServerModuleDto> modules, ServerModuleStoreSourcePageDto sources)
    {
        var state = modules.OrderBy(module => module.Id, StringComparer.Ordinal).Select(module => new
        {
            module.Id, module.DesiredRevision, module.EffectiveRevision, module.ProcessGeneration,
            module.DesiredVersion, module.EffectiveVersion, module.InstalledVersion, module.ArtifactDigest,
            module.DesiredEnabled, module.EffectiveEnabled, module.RecoveryState, module.RestartRequired,
            module.PendingOperationId
        });
        var catalog = new
        {
            sources.CatalogRevision,
            Sources = sources.Sources.OrderBy(source => source.SourceId, StringComparer.Ordinal)
        };
        return new(modules[0].ServiceId!, Digest(state), Digest(catalog));
    }

    private static string Digest<T>(T value) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private static DeploymentPackageKey Key(string id) => new(DeploymentPackageKind.ServerModule, id);

    internal static string? EffectivePackageVersion(ServerModuleDto module)
    {
        if (RuntimeSemanticVersion.TryParse(module.EffectiveVersion, out var version)) return version.Normalized;
        // Bundled modules advertise the host assembly version. A zero revision has the same
        // precedence as its three-component package version.
        return module.Packaging == "bundled" && Version.TryParse(module.EffectiveVersion, out var assembly)
            && assembly.Revision == 0 ? assembly.ToString(3) : null;
    }

    private static DeploymentRequirement ToRequirement(ServerModuleReleaseDependencyDto dependency) =>
        new(Key(dependency.ModuleId), dependency.MinimumVersion, dependency.MaximumVersionExclusive);

    private static string Identity(string sourceId, string releaseId, string digest) =>
        $"{sourceId}/{releaseId}/{digest}";

    private static ImmutableDictionary<string, string> Capabilities(IEnumerable<string> capabilities) =>
        capabilities.Distinct(StringComparer.Ordinal).ToImmutableDictionary(name => name, _ => "", StringComparer.Ordinal);
}
