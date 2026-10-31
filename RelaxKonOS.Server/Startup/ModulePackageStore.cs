using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Server.Startup;

/// <summary>Read-only search across every accepted signed module catalog. It never downloads or installs.</summary>
internal static class ModulePackageStore
{
    internal static ServerModuleStorePageDto Search(string contentRoot, IConfiguration configuration,
        IHostEnvironment environment, string? query, string? sourceId, int offset, int limit, DateTimeOffset now)
    {
        if (query is { Length: > 128 } || sourceId is not null && !ModulePackageHostConfiguration.Identifier(sourceId)
            || offset is < 0 or > 2048 || limit is < 1 or > 50)
            throw new ModulePackageException("module-store-request-invalid");
        var serviceId = ModuleManagementStore.ReadRecoveryReport(contentRoot).ServiceId;
        var document = ModulePackageCatalogStore.Read(contentRoot, serviceId);
        var context = ModulePackageHostConfiguration.Context(serviceId, configuration, environment);
        var items = new List<ServerModuleStoreItemDto>();
        foreach (var id in document.Sources.Keys)
        {
            if (sourceId is not null && !string.Equals(id, sourceId, StringComparison.Ordinal)) continue;
            try
            {
                var policy = ModulePackageHostConfiguration.Source(configuration, id);
                var verified = ModulePackageCatalogStore.ReadCurrentCatalog(document, policy, context, now);
                var revokedIds = verified.NextHistory.RevokedReleaseIds.ToHashSet(StringComparer.Ordinal);
                var revokedDigests = verified.NextHistory.RevokedDigests.ToHashSet(StringComparer.Ordinal);
                foreach (var release in verified.Catalog.Releases)
                {
                    var manifest = release.Manifest;
                    if (!Matches(manifest, query)) continue;
                    var revoked = revokedIds.Contains(manifest.ReleaseId) || revokedDigests.Contains(release.ArchiveSha256);
                    var rid = string.Equals(manifest.Rid, context.Rid, StringComparison.Ordinal);
                    var api = ParseVersion(manifest.HostApiMinimum) <= ParseVersion(context.HostApiVersion)
                        && ParseVersion(context.HostApiVersion) < ParseVersion(manifest.HostApiMaximumExclusive);
                    List<ServerModuleBlocker> blockers = [];
                    if (revoked) blockers.Add(new("module-package-release-revoked", manifest.ModuleId));
                    if (!rid) blockers.Add(new("module-package-rid-mismatch", manifest.ModuleId));
                    if (!api) blockers.Add(new("module-package-host-api-incompatible", manifest.ModuleId));
                    items.Add(new ServerModuleStoreItemDto(policy.SourceId, manifest.ModuleId, manifest.Version,
                        manifest.ReleaseId, manifest.Rid, manifest.PublisherId, policy.Purpose,
                        revoked ? "revoked" : policy.Purpose == "official" ? "official" : "development",
                        release.ArchiveSha256, release.ManifestSha256, release.ArchiveBytes,
                        manifest.HostApiMinimum, manifest.HostApiMaximumExclusive,
                        manifest.Dependencies.Select(dependency => new ServerModuleReleaseDependencyDto(dependency.ModuleId,
                            dependency.MinimumVersion, dependency.MaximumVersionExclusive)).ToArray(),
                        manifest.Capabilities, rid, api, revoked, blockers));
                }
            }
            catch (ModulePackageException) { }
        }
        var ordered = items.OrderBy(item => item.SourceId == "official" ? 0 : 1).ThenBy(item => item.ModuleId,
                StringComparer.Ordinal).ThenByDescending(item => ParseVersion(item.Version)).ThenBy(item => item.ReleaseId,
                StringComparer.Ordinal).ToArray();
        var page = ordered.Skip(offset).Take(limit).ToArray();
        return new(page, offset + page.Length < ordered.Length ? offset + page.Length : null, []);
    }

    internal static ServerModuleStoreSourcePageDto Sources(string contentRoot, IConfiguration configuration,
        IHostEnvironment environment, DateTimeOffset now)
    {
        var serviceId = ModuleManagementStore.ReadRecoveryReport(contentRoot).ServiceId;
        var document = ModulePackageCatalogStore.Read(contentRoot, serviceId);
        var context = ModulePackageHostConfiguration.Context(serviceId, configuration, environment);
        var sources = new List<ServerModuleStoreSourceDto>();
        foreach (var source in document.Sources.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var saved = source.Value;
            var catalogUri = configuration["ModulePackages:Sources:" + source.Key + ":CatalogUri"];
            try
            {
                var policy = ModulePackageHostConfiguration.Source(configuration, source.Key);
                try
                {
                    var verified = ModulePackageCatalogStore.ReadCurrentCatalog(document, policy, context, now);
                    sources.Add(new(policy.SourceId, policy.Purpose, "current", saved.History.Sequence,
                        saved.History.PolicyRevision, verified.Catalog.Releases.Length, saved.History.RevokedReleaseIds.Length,
                        !string.IsNullOrWhiteSpace(catalogUri), null));
                }
                catch (ModulePackageException error) when (error.ProblemCode == "module-package-source-refresh-required")
                {
                    sources.Add(new(policy.SourceId, policy.Purpose, "refresh-required", saved.History.Sequence,
                        saved.History.PolicyRevision, 0, saved.History.RevokedReleaseIds.Length,
                        !string.IsNullOrWhiteSpace(catalogUri), error.ProblemCode));
                }
            }
            catch (ModulePackageException error)
            {
                sources.Add(new(source.Key, "unknown", "unavailable", saved.History.Sequence,
                    saved.History.PolicyRevision, 0, saved.History.RevokedReleaseIds.Length,
                    !string.IsNullOrWhiteSpace(catalogUri), error.ProblemCode));
            }
        }
        return new(sources);
    }

    private static bool Matches(ModulePackageManifest manifest, string? query) =>
        string.IsNullOrWhiteSpace(query)
        || manifest.ModuleId.Contains(query, StringComparison.OrdinalIgnoreCase)
        || manifest.Version.Contains(query, StringComparison.OrdinalIgnoreCase)
        || manifest.ReleaseId.Contains(query, StringComparison.OrdinalIgnoreCase)
        || manifest.PublisherId.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static Version ParseVersion(string value) => System.Version.Parse(value);
}
