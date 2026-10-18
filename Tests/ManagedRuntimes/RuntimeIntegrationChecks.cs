using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;

internal static class RuntimeIntegrationChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var source = new RuntimeSource("integration", "sing-box", "test", RuntimeSourceKind.HttpsManifest, "https://example.com/catalog/", "manifest.json", "key-1",
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), true, 0, [RuntimeChannel.Stable, RuntimeChannel.Prerelease],
            65536, 86400, ["https://example.com/artifacts/"], [], []);
        var artifact = new RuntimeArtifact("a-1", new string('a', 64), 1024, "zip", "sing-box.exe", "key-1",
            new(1024, 8, [new("sing-box.exe", 4, new string('b', 64), true)]));
        var stable = new ManifestRelease("r-1", "sing-box", "v1.14.3", RuntimeChannel.Stable,
            new("windows", "amd64", null, null, RuntimeInstallMode.Archive, "scm"), artifact,
            "https://example.com/artifacts/sing-box.zip", now);
        var preview = stable with { ReleaseId = "r-2", OriginalVersion = "v1.15.0-alpha.1", Channel = RuntimeChannel.Prerelease };
        var manifest = new RuntimeManifest(1, source.SourceId, 1, now, now.AddHours(1), [stable, preview], []);
        var payload = RuntimeJson.Serialize(manifest);
        var signed = RuntimeCatalogVerifier.SignatureContext.ToArray().Concat(payload).ToArray();
        var envelope = RuntimeJson.Serialize(new SignedRuntimeCatalog(1, source.KeyId, Convert.ToBase64String(payload),
            Convert.ToBase64String(key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))));
        var root = Path.Combine(Path.GetTempPath(), "relaxkonos-runtime-integration-" + Guid.NewGuid().ToString("N"));
        // Exercise the actual SocketsHttpHandler callback, with no connection to the forbidden host.
        var loopback = source with { BaseUri = "https://127.0.0.1/catalog/" };
        try
        {
            await new RuntimeMetadataDownloader().DownloadAsync(loopback, default);
            throw new Exception("Loopback metadata must be blocked before connect");
        }
        catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.address_forbidden") { count++; }
        try
        {
            var store = new RuntimeSourceStore(root, path => Directory.CreateDirectory(path));
            store.Confirm(source, 0, source.ConfirmationFingerprint, "test-admin", now);
            var downloads = 0;
            var failDownload = false;
            var changeTrust = false;
            Task<byte[]> Download(RuntimeSource policy, CancellationToken ct)
            {
                downloads++;
                if (changeTrust)
                {
                    var changed = policy with { Enabled = false };
                    store.Confirm(changed, 1, changed.ConfirmationFingerprint, "test-admin", now);
                }
                if (failDownload) throw new RuntimePolicyException("runtime.network_unavailable");
                return Task.FromResult(envelope);
            }
            var transport = new TestTransport(request => RuntimeSourceOperations.ExecuteAsync(request, store, Download));
            var service = new RuntimeCatalogService(transport);
            var sources = await service.SourcesAsync(default);
            Check(sources.Single().Identity.TrustRevision == 1 && downloads == 0, "Source list requires no network or installation");
            var missing = await service.CatalogAsync(source.SourceId, false, false, default);
            Check(missing.Catalog is null && missing.ProblemCode == "runtime.catalog_missing", "Uninstalled source exposes missing catalog explicitly");
            var refreshed = await service.CatalogAsync(source.SourceId, true, false, default);
            Check(downloads == 1 && refreshed.Fresh && refreshed.ProblemCode is null, "Helper verifies and persists refreshed catalog");
            Check(refreshed.Catalog!.Releases.Count == 1 && refreshed.Catalog.Releases.Single().Channel == RuntimeChannel.Stable,
                "REST service hides prerelease by default");
            Check(!refreshed.Catalog.Releases.Single().ConfigurationSupported, "Discovery does not enable unsupported lifecycle adapter");
            Check((await service.CatalogAsync(source.SourceId, false, true, default)).Catalog!.Releases.Count == 2,
                "Explicit prerelease view includes both channels");
            Check((await service.CatalogAsync(source.SourceId, false, true, default)).Catalog!.Releases.First().ReleaseId == preview.ReleaseId,
                "sing-box catalog is ordered by numeric version precedence rather than publisher list order");
            failDownload = true;
            var failed = await service.CatalogAsync(source.SourceId, true, false, default);
            Check(failed.Fresh && failed.Catalog!.Revision == 1 && failed.ProblemCode == "runtime.network_unavailable",
                "Refresh failure preserves valid cache with failure code");
            var invalid = await RuntimeSourceOperations.ExecuteAsync(new(PrivilegedOperationKind.RuntimeCatalogRefresh,
                Path: "forbidden", RuntimeSourceId: source.SourceId), store, Download);
            Check(!invalid.Success && invalid.ProblemCode == PrivilegedProblemCode.InvalidRequest && downloads == 2,
                "Mixed Helper fields rejected before download");
            var sourceMutation = await RuntimeSourceOperations.ExecuteAsync(new(PrivilegedOperationKind.FileWrite,
                RuntimeSourceId: source.SourceId), store, Download);
            Check(!sourceMutation.Success, "Source handler has no trust provisioning verb");
            failDownload = false;
            changeTrust = true;
            var changedDuringDownload = await service.CatalogAsync(source.SourceId, true, false, default);
            Check(!changedDuringDownload.Fresh && changedDuringDownload.ProblemCode == "runtime.trust_revision_conflict"
                && !changedDuringDownload.Source.Identity.Enabled, "Trust revision is rechecked after network IO");
            var disabled = await service.CatalogAsync(source.SourceId, false, false, default);
            Check(!disabled.Fresh && disabled.Catalog is not null, "Disabled source retains display cache");

            var builder = WebApplication.CreateBuilder();
            builder.Services.AddSingleton(service);
            await using var app = builder.Build();
            app.MapRuntimeCatalogEndpoints();
            var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToArray();
            Check(endpoints.Length == 9 && endpoints.Any(e => e.RoutePattern.RawText == RuntimeRoutes.SingBoxInstallation)
                && endpoints.Any(e => e.RoutePattern.RawText == RuntimeRoutes.SingBoxPlan),
                "Source/catalog, sing-box installation observation and frozen planning are exposed without a second execution ledger");
            foreach (var route in new[] { RuntimeRoutes.SingBoxInstallation, RuntimeRoutes.SingBoxRecovery })
                Check(endpoints.Count(e => e.RoutePattern.RawText == route) == 1, "Recovery observation route is readable exactly once: " + route);
            foreach (var route in new[] { RuntimeRoutes.SingBoxRecoveryRetry, RuntimeRoutes.SingBoxRecoveryStop, RuntimeRoutes.SingBoxRecoveryClose })
                Check(endpoints.Count(e => e.RoutePattern.RawText == route) == 1
                    && endpoints.Single(e => e.RoutePattern.RawText == route).Metadata.GetMetadata<IHttpMethodMetadata>()!
                        .HttpMethods.SequenceEqual(["POST"]),
                    "Recovery action route is a single POST: " + route);
            foreach (var endpoint in endpoints)
            {
                var isPost = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("POST");
                Check(endpoint.Metadata.GetMetadata<IAuthorizeData>()!.Policy == (isPost ? RuntimeCatalogEndpoints.ManagePolicy : RuntimeCatalogEndpoints.ReadPolicy),
                    "Route authorization " + endpoint.RoutePattern.RawText);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        return count;
    }

    private sealed class TestTransport(Func<PrivilegedOperationRequest, Task<PrivilegedOperationResult>> execute) : IPrivilegedOperationTransport
    {
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default) => execute(request);
    }
}
