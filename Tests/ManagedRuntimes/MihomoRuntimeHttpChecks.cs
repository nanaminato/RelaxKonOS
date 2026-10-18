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
        builder.Services.AddSingleton(new MihomoRuntimeService(fixture, store, new(store, fixture, fixture), fixture));
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
            await store.BeginInstallationAsync(Guid.NewGuid(), 0, material, new(request, "mihomo", release, null), default);
            using (var pending = await client.PostAsJsonAsync(RuntimeRoutes.MihomoPlan, selection))
                Check(pending.IsSuccessStatusCode && (await pending.Content.ReadFromJsonAsync<RuntimePlanResult>())!.Problems.Contains("runtime.publication_unconfirmed")
                    && fixture.Calls == beforeUserMode, "Pending Server publication blocks plans before a second Helper operation");
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
            if (name is "installer" or "wrong-install") { claims.Add(new("role", "controller")); claims.Add(new("installation:install", name == "installer" ? "mihomo" : "singbox")); }
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, "fixture")), "fixture")));
        }
    }
    private sealed class Fixture(string root) : IPrivilegedOperationTransport, IServerModeResolver, IProxyPlatformPaths, IProxyControllerSecretStore
    {
        internal int Calls;
        internal bool SystemMode = true;
        internal PrivilegedOperationRequest? Last;
        internal RuntimeInstallation Installation { get; } = new(Guid.NewGuid(), "http-host", "mihomo", 0, RuntimeOwnership.Unknown,
            new("linux", "amd64", null, null, RuntimeInstallMode.Archive, "systemd"), null, false);
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            Calls++; Last = request;
            object value = request.Operation switch
            {
                PrivilegedOperationKind.MihomoRuntimeQuery => new RuntimeInstallationSnapshot(Installation, null, null, null, null, null, "runtime.state_unknown"),
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
