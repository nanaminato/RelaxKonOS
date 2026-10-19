using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

internal static class MihomoRuntimeHttpChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        var root = Path.Combine(Path.GetTempPath(), "mihomo-runtime-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fixture = new Fixture(root);
        var store = new MihomoPublicationStore(fixture, new EphemeralDataProtectionProvider());
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, Authentication>("fixture", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(RuntimeCatalogEndpoints.ReadPolicy, policy => policy.RequireClaim("permission", "read"));
            options.AddPolicy(RuntimeCatalogEndpoints.ManagePolicy, policy => policy.RequireClaim("permission", "manage"));
        });
        builder.Services.AddSingleton<IServerModeResolver>(fixture);
        builder.Services.AddSingleton(new RuntimeCatalogService(fixture));
        var authorization = new MihomoRuntimeAuthorizationProvider(store, fixture, fixture);
        var runtimeService = new MihomoRuntimeService(fixture, store, authorization, fixture);
        builder.Services.AddSingleton(runtimeService);
        builder.Services.AddSingleton(new MihomoRuntimeRecoveryService(store, runtimeService,
            new(store, authorization, runtimeService, fixture), new(store, authorization, runtimeService, fixture),
            new(store, runtimeService, fixture, MihomoRemovalHostFixture.Files(fixture))));
        builder.Services.AddSingleton<IHostElevationSessionStore, Elevations>();
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.MapRuntimeCatalogEndpoints();
        try
        {
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            using (var unauthorized = await client.GetAsync(RuntimeRoutes.MihomoInstallation))
                Check(unauthorized.StatusCode == HttpStatusCode.Unauthorized && fixture.Calls == 0, "Anonymous runtime reads cannot contact Helper");
            client.DefaultRequestHeaders.Authorization = new("Bearer", "read");
            var observed = await client.GetFromJsonAsync<RuntimeInstallationSnapshot>(RuntimeRoutes.MihomoInstallation);
            Check(observed is { Installation.Ownership: RuntimeOwnership.Unknown, ProblemCode: "runtime.state_unknown" },
                "HTTP preserves an unknown Helper observation instead of reporting not installed");
            var request = new RuntimePlanRequest(fixture.Installation.InstallationId, fixture.Installation.HostId, RuntimeChange.Install,
                0, 1, 1, "release", "http-plan");
            var selection = new RuntimePlanSelection("source", request);
            client.DefaultRequestHeaders.Authorization = new("Bearer", "manage");
            using (var forbidden = await client.PostAsJsonAsync(RuntimeRoutes.MihomoPlan, selection))
                Check(forbidden.StatusCode == HttpStatusCode.Forbidden && fixture.Calls == 1, "RuntimeManage alone cannot bypass the Mihomo install claim");
            client.DefaultRequestHeaders.Authorization = new("Bearer", "wrong-install");
            using (var wrongTarget = await client.PostAsJsonAsync(RuntimeRoutes.MihomoPlan, selection))
                Check(wrongTarget.StatusCode == HttpStatusCode.Forbidden && fixture.Calls == 1, "A different runtime's install restriction cannot plan Mihomo");
            client.DefaultRequestHeaders.Authorization = new("Bearer", "installer");
            using (var invalidSource = await client.PostAsJsonAsync(RuntimeRoutes.MihomoPlan, selection with { SourceId = "../source" }))
                Check(invalidSource.StatusCode == HttpStatusCode.BadRequest && fixture.Calls == 1, "Invalid source shape is rejected before Helper");
            using (var unknownField = await client.PostAsJsonAsync(RuntimeRoutes.MihomoPlan,
                new { SourceId = "source", Request = request, ExecutablePath = "/bin/sh" }))
                Check(unknownField.StatusCode == HttpStatusCode.BadRequest && fixture.Calls == 1, "The current plan request refuses executable/path extensions");
            using (var allowed = await client.PostAsJsonAsync(RuntimeRoutes.MihomoPlan, selection))
                Check(allowed.IsSuccessStatusCode && (await allowed.Content.ReadFromJsonAsync<RuntimePlanResult>())!.Problems.Contains("runtime.release_untrusted")
                    && fixture.Last?.Operation == PrivilegedOperationKind.MihomoRuntimePlan,
                    "An authorized plan returns independent Helper admission without executing a lifecycle operation");
            using (var removal = await client.PostAsJsonAsync(RuntimeRoutes.MihomoPlan,
                new RuntimePlanSelection(null, request with { Change = RuntimeChange.Uninstall, ReleaseId = null })))
                Check(removal.IsSuccessStatusCode && fixture.Last?.RuntimeLifecycle?.SourceId == "removal",
                    "Removal planning can omit an expired or deleted source");
            var beforeUserMode = fixture.Calls;
            client.DefaultRequestHeaders.Authorization = new("Bearer", "read");
            fixture.SystemMode = false;
            using (var userMode = await client.GetAsync(RuntimeRoutes.MihomoInstallation))
                Check(userMode.StatusCode == HttpStatusCode.Forbidden && fixture.Calls == beforeUserMode, "User Mode cannot contact privileged Mihomo operations");
            fixture.SystemMode = true;
            client.DefaultRequestHeaders.Authorization = new("Bearer", "installer");
            var material = new MihomoPublishedRuntime("# pending\n", [], 9090, "http-fixture-secret", [new("127.0.0.1", 7890)]);
            var release = new RuntimeRelease("release", "mihomo", "v1.19.30", "1.19.30", RuntimeChannel.Stable, "source", fixture.Installation.Target,
                new("artifact", new string('a', 64), 4096, "gz", "mihomo", "signed-manifest:key", new(4096, 1, [new("mihomo", 1, new string('b', 64), true)])),
                "mihomo-v1", true, RuntimeTrust.Confirmed, RuntimeEvidence.Unverified, null);
            var pendingOperation = Guid.NewGuid();
            await store.BeginInstallationAsync(pendingOperation, 0, material, new(request, "mihomo", release, null), default);
            using (var pending = await client.PostAsJsonAsync(RuntimeRoutes.MihomoPlan, selection))
                Check(pending.IsSuccessStatusCode && (await pending.Content.ReadFromJsonAsync<RuntimePlanResult>())!.Problems.Contains("runtime.publication_unconfirmed")
                    && fixture.Calls == beforeUserMode, "Pending Server publication blocks plans before a second Helper operation");
            client.DefaultRequestHeaders.Authorization = new("Bearer", "read");
            fixture.Observation = new(fixture.Installation with { Ownership = RuntimeOwnership.Managed, StateRevision = 1, ActiveReleaseId = release.ReleaseId },
                "1.19.30", Guid.NewGuid(), MihomoPublicationStore.Identity(material.Yaml), null, null, null);
            var pendingObservation = await client.GetFromJsonAsync<RuntimeInstallationSnapshot>(RuntimeRoutes.MihomoInstallation);
            Check(pendingObservation is { Installation.RecoveryRequired: true, MeasuredVersion: "1.19.30", PendingStage: null, ProblemCode: "runtime.publication_unconfirmed" }
                && pendingObservation.PendingOperationId == pendingOperation && fixture.Last?.Operation == PrivilegedOperationKind.MihomoRuntimeQuery,
                "Public observation reports unresolved Server work even after a healthy native commit");
            Check((await store.ReadAsync(default)).Pending?.OperationId == pendingOperation,
                "Runtime observation never confirms or compensates a pending Server publication");
            var runtime = app.Services.GetRequiredService<MihomoRuntimeService>();
            var recovery = (await client.GetFromJsonAsync<RuntimeRecoverySnapshot>(RuntimeRoutes.MihomoRecovery))!;
            Check(recovery is { Required: true, Change: RuntimeChange.Install, Stage: null,
                    RecoveryProblemCode: null, ProblemCode: "runtime.publication_unconfirmed" }
                && recovery.OperationId == pendingOperation,
                "Recovery observation retains Server commit work without inventing a failed native compensation");
            var beforeRecovery = fixture.Calls;
            foreach (var identity in new[] { "manage", "wrong-install", "installer" })
            {
                client.DefaultRequestHeaders.Authorization = new("Bearer", identity);
                using var refused = await client.PostAsJsonAsync(RuntimeRoutes.MihomoRecoveryRetry,
                    new RuntimeRecoveryActionRequest(pendingOperation));
                Check(refused.StatusCode == HttpStatusCode.Forbidden && fixture.Calls == beforeRecovery,
                    "Recovery requires Mihomo install permission and its matching elevation: " + identity);
            }
            client.DefaultRequestHeaders.Authorization = new("Bearer", "elevated-installer");
            using (var wrongOperation = await client.PostAsJsonAsync(RuntimeRoutes.MihomoRecoveryRetry,
                new RuntimeRecoveryActionRequest(Guid.NewGuid())))
                Check(wrongOperation.StatusCode == HttpStatusCode.Conflict && fixture.Calls == beforeRecovery,
                    "Elevated recovery refuses a different Server pending operation before contacting Helper");
            using (var emptyOperation = await client.PostAsJsonAsync(RuntimeRoutes.MihomoRecoveryRetry,
                new RuntimeRecoveryActionRequest(Guid.Empty)))
                Check(emptyOperation.StatusCode == HttpStatusCode.BadRequest && fixture.Calls == beforeRecovery,
                    "Recovery rejects an empty operation identity");
            client.DefaultRequestHeaders.Authorization = new("Bearer", "read");
            var otherRootOperation = Guid.NewGuid();
            fixture.Observation = fixture.Observation with { PendingOperationId = otherRootOperation };
            var conflicting = await client.GetFromJsonAsync<RuntimeInstallationSnapshot>(RuntimeRoutes.MihomoInstallation);
            Check(conflicting?.ProblemCode == "runtime.recovery_target_conflict" && conflicting.PendingOperationId == otherRootOperation,
                "Public observation retains an actual conflicting Helper operation instead of relabeling it");
            fixture.Recovery = recovery with { OperationId = otherRootOperation, ProblemCode = "runtime.recovery_required" };
            var recoveryConflict = await runtime.RecoveryAsync(default);
            Check(recoveryConflict.OperationId == otherRootOperation && recoveryConflict.ProblemCode == "runtime.recovery_target_conflict",
                "Recovery observation preserves conflicting native operation identity");
            await File.WriteAllTextAsync(Path.Combine(fixture.GetStateDirectory(), "mihomo-publication.protected"), "corrupt");
            var invalidRecovery = await runtime.RecoveryAsync(default);
            Check(invalidRecovery.Required && invalidRecovery.ProblemCode == "runtime.publication_state_invalid"
                && invalidRecovery.OperationId == otherRootOperation,
                "Corrupt Server authority cannot clear a native recovery operation");
            var unknown = await client.GetFromJsonAsync<RuntimeInstallationSnapshot>(RuntimeRoutes.MihomoInstallation);
            Check(unknown is { Installation.Ownership: RuntimeOwnership.Unknown, Installation.RecoveryRequired: true,
                Installation.ActiveReleaseId: null, MeasuredVersion: null, ActiveRevisionId: null, ProblemCode: "runtime.publication_state_invalid" },
                "Unreadable Server authority withdraws ownership/version proof and never reports not installed");
        }
        finally { await app.StopAsync(); Directory.Delete(root, true); }
        return count;
    }

    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var token = Request.Headers.Authorization.ToString();
            if (!token.StartsWith("Bearer ", StringComparison.Ordinal)) return Task.FromResult(AuthenticateResult.NoResult());
            var name = token[7..];
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "http-fixture"), new("permission", name == "read" ? "read" : "manage") };
            if (name is "installer" or "wrong-install" or "elevated-installer") { claims.Add(new("role", "controller")); claims.Add(new("installation:install", name != "wrong-install" ? "mihomo" : "singbox")); }
            if (name == "elevated-installer") claims.Add(new("test-elevated", "true"));
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, "fixture")), "fixture")));
        }
    }
    private sealed class Elevations : IHostElevationSessionStore
    {
        public bool IsGranted(ClaimsPrincipal principal, HostElevationCapability capability, string target) =>
            principal.HasClaim("permission", "manage") && principal.HasClaim("test-elevated", "true")
            && capability == HostElevationCapability.MihomoInstall && target == "mihomo";
        public DateTimeOffset Grant(ClaimsPrincipal principal, HostElevationCapability capability, string target,
            bool includeDescendants, string authenticationMethod, string? correlationId = null) => throw new NotSupportedException();
        public void Revoke(ClaimsPrincipal principal) => throw new NotSupportedException();
    }
    private sealed class Fixture(string root) : IPrivilegedOperationTransport, IServerModeResolver, IProxyPlatformPaths, IProxyControllerSecretStore
    {
        internal int Calls;
        internal bool SystemMode = true;
        internal PrivilegedOperationRequest? Last;
        internal RuntimeInstallationSnapshot? Observation;
        internal RuntimeRecoverySnapshot? Recovery;
        internal RuntimeInstallation Installation { get; } = new(Guid.NewGuid(), "http-host", "mihomo", 0, RuntimeOwnership.Unknown,
            new("linux", "amd64", null, null, RuntimeInstallMode.Archive, "systemd"), null, false);
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            Calls++; Last = request;
            object value = request.Operation switch
            {
                PrivilegedOperationKind.MihomoRuntimeQuery => Observation ?? new RuntimeInstallationSnapshot(Installation, null, null, null, null, null, "runtime.state_unknown"),
                PrivilegedOperationKind.MihomoRuntimeRecoveryQuery => Recovery ?? new RuntimeRecoverySnapshot(false, null, null, null, false, null, null, null, null),
                PrivilegedOperationKind.MihomoRuntimePlan => new RuntimePlanResult(null, ["runtime.release_untrusted"]),
                _ => throw new Exception("HTTP read/plan route executed an unrequested Helper action")
            };
            return Task.FromResult(new PrivilegedOperationResult(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value))));
        }
        public ServerMode Mode => SystemMode ? ServerMode.System : ServerMode.User;
        public bool Supports(ServerHostFeature feature) => SystemMode;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
        public string GetEngineVersionsDirectory(string engineId) => Path.Combine(root, "versions");
        public string GetProtectedConfigurationDirectory() => Path.Combine(root, "config");
        public string GetEngineDataDirectory(string engineId) => Path.Combine(root, "data");
        public string GetStateDirectory() => Path.Combine(root, "state");
        public string GetSanitizedLogDirectory() => Path.Combine(root, "logs");
        public Task<string?> ReadExistingAsync(CancellationToken ct) => throw new Exception("HTTP read/plan fetched credentials");
        public Task<string> GetOrCreateAsync(CancellationToken ct) => throw new Exception("HTTP read/plan generated credentials");
    }
}
