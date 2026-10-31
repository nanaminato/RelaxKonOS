using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Server.Startup;

internal static class ModulePackageReleaseDirectory
{
    internal static ServerModuleReleasePageDto Read(ServerModuleContext host, ModuleManagementStore management, ServerModules modules,
        string moduleId, string sourceId, int offset, int limit, DateTimeOffset now, string? expectedCatalogRevision = null)
    {
        if (!ModulePackageHostConfiguration.Identifier(moduleId) || offset is < 0 or > 2048 || limit is < 1 or > 25
            || offset > 0 && expectedCatalogRevision is null)
            throw new ModuleManagementException("module-request-invalid", 400);
        var policy = ModulePackageHostConfiguration.Source(host.Configuration, sourceId);
        if (policy.AuthorizedModuleIds is null || !policy.AuthorizedModuleIds.Contains(moduleId, StringComparer.Ordinal))
            throw new ModuleManagementException("module-not-found", 404);
        var context = ModulePackageHostConfiguration.Context(management.ServiceId, host.Configuration, host.Environment);
        var document = ModulePackageCatalogStore.Read(host.ContentRootPath, context.ServiceId);
        if (expectedCatalogRevision is not null && expectedCatalogRevision != document.Revision)
            throw new ModuleManagementException("module-catalog-revision-conflict");
        var verified = ModulePackageCatalogStore.ReadCurrentCatalog(document, policy, context, now);
        var candidates = verified.Catalog.Releases.Where(r => r.Manifest.ModuleId == moduleId)
            .OrderByDescending(r => Version.Parse(r.Manifest.Version)).ThenBy(r => r.Manifest.Rid, StringComparer.Ordinal)
            .ThenBy(r => r.Manifest.ReleaseId, StringComparer.Ordinal).ToArray();
        var hostVersion = Version.Parse(context.HostApiVersion);
        var revokedIds = verified.NextHistory.RevokedReleaseIds.ToHashSet(StringComparer.Ordinal);
        var revokedDigests = verified.NextHistory.RevokedDigests.ToHashSet(StringComparer.Ordinal);
        var items = candidates.Skip(offset).Take(limit).Select(release =>
        {
            var m = release.Manifest;
            var revoked = revokedIds.Contains(m.ReleaseId) || revokedDigests.Contains(release.ArchiveSha256);
            var rid = m.Rid == context.Rid;
            var api = hostVersion >= Version.Parse(m.HostApiMinimum) && hostVersion < Version.Parse(m.HostApiMaximumExclusive);
            List<ServerModuleBlocker> blockers = [new("module-package-artifact-verification-required", moduleId)];
            if (revoked) blockers.Add(new("module-package-release-revoked", moduleId));
            if (!rid) blockers.Add(new("module-package-rid-mismatch", moduleId));
            if (!api) blockers.Add(new("module-package-host-api-incompatible", moduleId));
            if (m.Dependencies.Length > 0) blockers.Add(new("module-package-dependency-verification-required", moduleId));
            return new ServerModuleReleaseDto(m.ReleaseId, moduleId, m.Version, m.Rid, m.PublisherId, m.Purpose,
                release.ManifestSha256, release.ArchiveSha256, release.ArchiveBytes, m.HostApiMinimum, m.HostApiMaximumExclusive,
                m.BuildId, m.SourceCommit, m.Dependencies.Select(d => new ServerModuleReleaseDependencyDto(d.ModuleId,
                    d.MinimumVersion, d.MaximumVersionExclusive)).ToArray(), m.Capabilities, revoked ? "revoked" : "catalog-verified",
                rid, api, false, blockers);
        }).ToArray();
        var reason = modules.IsInstalled(moduleId) ? "module-package-actions-unsupported"
            : modules.KnownModuleIds.Contains(moduleId) ? "module-packaging-bundled"
            : "module-package-installer-api-unavailable";
        return new(context.ServiceId, moduleId, sourceId, document.Revision, verified.Catalog.Sequence, verified.PayloadSha256,
            verified.Catalog.IssuedAt, verified.Catalog.ExpiresAt, now, context.Rid, context.HostApiVersion, "signed-catalog", false,
            [new(reason, moduleId)], items, offset + items.Length < candidates.Length ? offset + items.Length : null);
    }
}
