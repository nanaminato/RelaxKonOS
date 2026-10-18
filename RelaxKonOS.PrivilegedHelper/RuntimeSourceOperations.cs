using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

internal static class RuntimeSourceOperations
{
    internal static Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request) =>
        ExecuteAsync(request, RuntimeSourceProtection.Store(), new RuntimeMetadataDownloader().DownloadAsync);

    internal static async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request,
        RuntimeSourceStore store, Func<RuntimeSource, CancellationToken, Task<byte[]>> download)
    {
        var clean = new PrivilegedOperationRequest(request.Operation, OperationId: request.OperationId,
            Correlation: request.Correlation, Version: request.Version, RuntimeSourceId: request.RuntimeSourceId);
        if (request.Operation is not (PrivilegedOperationKind.RuntimeSources or PrivilegedOperationKind.RuntimeCatalogGet or PrivilegedOperationKind.RuntimeCatalogRefresh)
            || clean != request || request.Operation == PrivilegedOperationKind.RuntimeSources && request.RuntimeSourceId is not null
            || request.Operation is PrivilegedOperationKind.RuntimeCatalogGet or PrivilegedOperationKind.RuntimeCatalogRefresh
                && !RuntimeValidation.Identifier(request.RuntimeSourceId))
            return new(false, 64, ProblemCode: PrivilegedProblemCode.InvalidRequest, Error: "runtime.request_invalid");
        try
        {
            if (request.Operation == PrivilegedOperationKind.RuntimeSources)
                return Json(store.List().Select(View).ToArray());
            var state = store.Read(request.RuntimeSourceId!) ?? throw new RuntimePolicyException("runtime.source_not_found");
            if (request.Operation == PrivilegedOperationKind.RuntimeCatalogGet) return Json(Snapshot(state, null));
            try
            {
                var envelope = await download(state.Source, CancellationToken.None);
                store.Accept(state.Source.SourceId, state.Source.TrustRevision, envelope, DateTimeOffset.UtcNow, ConfigurationSupport);
                return Json(Snapshot(store.Read(state.Source.SourceId)!, null));
            }
            catch (RuntimePolicyException ex)
            {
                // A failed refresh returns the last good cache and its freshness alongside the failure.
                return Json(Snapshot(store.Read(state.Source.SourceId)!, ex.ProblemCode));
            }
        }
        catch (RuntimePolicyException ex) { return new(false, 1, ProblemCode: PrivilegedProblemCode.Conflict, Error: ex.ProblemCode); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { return new(false, 1, ProblemCode: PrivilegedProblemCode.AccessDenied, Error: "runtime.source_store_unavailable"); }
    }

    // Discovery is safe before lifecycle adapters are connected. Catalog publishers cannot enable support:
    // the capability comes from this build's measured native-format range, never from the signed manifest.
    internal static RuntimeConfigurationSupport ConfigurationSupport(ManifestRelease release)
    {
        if (release.RuntimeId != "sing-box" || !SingBoxNativeValidation.TryTag(release.OriginalVersion, out var version))
            return new("", "", false);
        if (version.IsPrerelease != (release.Channel == RuntimeChannel.Prerelease))
            throw new RuntimePolicyException("runtime.release_channel_invalid");
        var capability = SingBoxRuntimeSupport.CapabilityFor(version.Normalized);
        return new(version.Normalized, capability ?? "", capability is not null);
    }

    internal static RuntimeCatalogSnapshot Snapshot(StoredRuntimeSource stored, string? problem)
    {
        var source = stored.Source;
        var catalog = stored.Cached?.Catalog;
        if (catalog is not null && source.RuntimeId == "sing-box")
            catalog = catalog with { Releases = catalog.Releases.OrderByDescending(r =>
                RuntimeSemanticVersion.TryParse(r.SortVersion, out var version) ? version : null).ToArray() };
        var fresh = catalog is not null && catalog.ExpiresAt > DateTimeOffset.UtcNow
            && catalog.TrustRevision == source.TrustRevision && source.Enabled;
        return new(View(stored), catalog,
            fresh, problem ?? (catalog is null ? "runtime.catalog_missing" : !source.Enabled ? "runtime.source_disabled"
                : catalog.TrustRevision != source.TrustRevision ? "runtime.trust_revision_conflict"
                : !fresh ? "runtime.catalog_expired" : null));
    }

    internal static RuntimeSourceView View(StoredRuntimeSource stored) => new(
        new(stored.Source.SourceId, stored.Source.Publisher, stored.Source.Enabled, RuntimeTrust.Confirmed, stored.Source.TrustRevision),
        stored.Source.RuntimeId, stored.Source.Kind, stored.Source.BaseUri, stored.Source.ConfirmationFingerprint, stored.History.HighWater);

    private static PrivilegedOperationResult Json<T>(T value) => new(true,
        OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)));
}
