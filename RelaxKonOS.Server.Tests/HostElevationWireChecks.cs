using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Settings;
using RelaxKonOS.Server.SystemPerformance;
using RelaxKonOS.Protocol.SystemMonitor;

public static class HostElevationWireChecks
{
    public static async Task RunAsync()
    {
        var privileges = new TestHostAccountPrivilegeService { Level = HostAccountPrivilege.HostAdministrator };
        var mode = new UploadSessionChecks.SystemMode();
        var state = new HostElevationSessionState();
        var store = new HostElevationSessionStore(privileges, mode, state);
        var administrator = new Administrator();
        var method = "system";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IServerModeResolver>(mode);
        builder.Services.AddSingleton<IHostAccountPrivilegeService>(privileges);
        builder.Services.AddSingleton(state);
        builder.Services.AddScoped<IHostElevationSessionStore, HostElevationSessionStore>();
        builder.Services.AddSingleton<IHostAdministratorAuthenticator>(administrator);
        builder.Services.AddSingleton<IHostEnvironmentService, EnvironmentTargets>();
        builder.Services.AddSingleton<OwnerDeviceKeyService>();
        var processes = new ProcessReads();
        builder.Services.AddSingleton<IProcessService>(processes);
        builder.Services.AddSingleton<IPerformanceSampler>(_ => throw new NotSupportedException("Performance is outside this HTTP fixture."));
        builder.Services.ConfigureHttpJsonOptions(options =>
        { foreach (var converter in RelaxKonOSJsonOptions.Default.Converters) options.SerializerOptions.Converters.Add(converter); });
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", "wire-user"), new Claim("jti", method + "-token"), new Claim("name", "alice"),
                new Claim("amr", method)], "test"));
            return next(context);
        });
        app.UseAuthorization(); app.MapPrivilegedEndpoints(); app.MapSystemMonitorEndpoints(); await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        async Task<bool> Grant(HostElevationRequest request, HttpStatusCode expected)
        {
            using var response = await http.PostAsJsonAsync(PrivilegedApiRoutes.Elevation, request, RelaxKonOSJsonOptions.Default);
            Assert(response.StatusCode == expected, "Unexpected elevation response: " + await response.Content.ReadAsStringAsync());
            return response.IsSuccessStatusCode;
        }
        var request = new HostElevationRequest(HostElevationCapability.HostEnvironmentChange, "host/environment/machine");
        await Grant(request, HttpStatusCode.OK);
        Assert(administrator.Calls == 0, "System administrator must not be challenged again");
        privileges.Level = HostAccountPrivilege.StandardUser;
        await Grant(request, HttpStatusCode.Forbidden);
        Assert(administrator.Calls == 1, "Revocation must be checked before reusing automatic authorization");
        privileges.Level = HostAccountPrivilege.HostAdministrator; method = "alias";
        await Grant(request, HttpStatusCode.Forbidden);
        await Grant(request with { AdministratorUsername = "alice", Password = "test-password" }, HttpStatusCode.OK);
        Assert(administrator.Calls == 3, "Alias requires explicit administrator credentials");
        // Manual machine grants must never authorize the user's store, or a different capability.
        if (OperatingSystem.IsWindows())
        {
            await Grant(request with { Target = "host/environment/user/owned-sid" }, HttpStatusCode.OK);
            Assert(administrator.Calls == 3, "Own Windows environment must not need administrator credentials");
        }
        await Grant(new(HostElevationCapability.HostIdentityChange, "host/identity"), HttpStatusCode.Forbidden);
        await Grant(request with { Target = "host/environment/user/another-sid" }, HttpStatusCode.Forbidden);
        await Grant(new(HostElevationCapability.FileRead, Path.GetFullPath("test")), HttpStatusCode.BadRequest);
        var selfSignedRequest = new HostElevationRequest(HostElevationCapability.CertificateCreateSelfSigned, "certificates/self-signed");
        await Grant(selfSignedRequest, HttpStatusCode.Forbidden);
        await Grant(selfSignedRequest with { AdministratorUsername = "alice", Password = "test-password" }, HttpStatusCode.OK);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "wire-user"), new Claim("jti", "alias-token"), new Claim("amr", "alias")], "test"));
        Assert(store.IsGranted(principal, HostElevationCapability.CertificateCreateSelfSigned, "certificates/self-signed"), "Certificate creation grant was not recognized");
        Assert(!store.IsGranted(principal, HostElevationCapability.CertificateCreateSelfSigned, "certificates/other"), "Certificate grant crossed targets");
        Assert(!store.IsGranted(principal, HostElevationCapability.NginxConfigurationWrite, "certificates/self-signed"), "Certificate grant crossed capabilities");
        var certificateTarget = $"certificates/{Guid.NewGuid():D}/server-https";
        var replacement = new HostElevationRequest(HostElevationCapability.CertificateReplaceServerHttps, certificateTarget);
        await Grant(replacement, HttpStatusCode.Forbidden);
        await Grant(replacement with { AdministratorUsername = "alice", Password = "test-password" }, HttpStatusCode.OK);
        Assert(store.IsGranted(principal, HostElevationCapability.CertificateReplaceServerHttps, certificateTarget), "Server HTTPS grant was not recognized");
        Assert(!store.IsGranted(principal, HostElevationCapability.CertificateReplaceServerHttps, $"certificates/{Guid.NewGuid():D}/server-https"), "Server HTTPS grant crossed certificate targets");
        Assert(!store.IsGranted(principal, HostElevationCapability.CertificateCreateSelfSigned, certificateTarget), "Server HTTPS grant crossed capabilities");
        method = "system"; privileges.Level = HostAccountPrivilege.HostAdministrator;
        var processGrant = new HostElevationRequest(HostElevationCapability.ProcessManage, "host/processes");
        var authenticationCalls = administrator.Calls;
        await Grant(processGrant, HttpStatusCode.OK);
        Assert(administrator.Calls == authenticationCalls, "Already authenticated administrator enters process mode without another password");
        using (var ordinary = await http.GetAsync(SystemMonitorApiRoutes.ProcessQuery))
            Assert(ordinary.IsSuccessStatusCode && !processes.LastElevated, "Ordinary request remains ordinary even for an administrator");
        using (var elevated = await http.GetAsync(SystemMonitorApiRoutes.ProcessQuery + "?elevated=true"))
            Assert(elevated.IsSuccessStatusCode && processes.LastElevated, "Authorized process query selects the helper context");
        privileges.Level = HostAccountPrivilege.StandardUser;
        var reads = processes.Calls;
        using (var revoked = await http.GetAsync(SystemMonitorApiRoutes.ProcessQuery + "?elevated=true"))
            Assert(revoked.StatusCode == HttpStatusCode.Forbidden && processes.Calls == reads, "Revoked admin cannot read a cached privileged snapshot");
        using (var icon = await http.GetAsync(SystemMonitorApiRoutes.ProcessIcon.Replace("{id}", "42") + "?startTime=2026-10-01T00:00:00Z&elevated=true"))
            Assert(icon.StatusCode == HttpStatusCode.Forbidden && processes.Calls == reads, "Privileged icon is authorized before lookup");
        using (var terminationRequest = new HttpRequestMessage(HttpMethod.Delete, SystemMonitorApiRoutes.ProcessKill.Replace("{id}", "42"))
            { Content = JsonContent.Create(new TerminateProcessRequest(DateTimeOffset.UtcNow, true), options: RelaxKonOSJsonOptions.Default) })
        using (var terminate = await http.SendAsync(terminationRequest))
            Assert(terminate.StatusCode == HttpStatusCode.Forbidden && processes.Calls == reads, "Unapproved termination never reaches the process service");
        await Grant(processGrant, HttpStatusCode.Forbidden);
        await Grant(processGrant with { AdministratorUsername = "alice", Password = "test-password" }, HttpStatusCode.OK);
        using (var authorized = await http.GetAsync(SystemMonitorApiRoutes.ProcessQuery + "?elevated=true"))
            Assert(authorized.IsSuccessStatusCode && processes.LastElevated, "Ordinary user enters administrator mode after exact process grant");
        await Grant(processGrant with { Target = "host/other" }, HttpStatusCode.BadRequest);
        method = "alias";
        using (var otherToken = await http.GetAsync(SystemMonitorApiRoutes.ProcessQuery + "?elevated=true"))
            Assert(otherToken.StatusCode == HttpStatusCode.Forbidden, "Process elevation cannot cross access-token identities");
        Console.WriteLine("Host elevation HTTP checks passed: administrator revalidation, alias challenge, exact grants and target ownership.");
    }

    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class ProcessReads : IProcessService
    {
        public int Calls { get; private set; }
        public bool LastElevated { get; private set; }
        public Task<ProcessPageDto> QueryAsync(int page, int pageSize, string? filter, string? sort, bool descending,
            bool elevated = false, CancellationToken cancellationToken = default)
        { Calls++; LastElevated = elevated; return Task.FromResult(new ProcessPageDto([], 0, DateTimeOffset.UtcNow)); }
        public Task<ProcessIconContent?> GetIconAsync(int id, DateTimeOffset time, bool elevated = false, CancellationToken cancellationToken = default)
        { Calls++; LastElevated = elevated; return Task.FromResult<ProcessIconContent?>(null); }
        public Task<KillProcessResultDto> KillAsync(int id, DateTimeOffset time, bool elevated = false, CancellationToken cancellationToken = default)
        { Calls++; LastElevated = elevated; return Task.FromResult(new KillProcessResultDto(false, false, "process.not_found", null)); }
    }
    private sealed class Administrator : IHostAdministratorAuthenticator
    {
        public int Calls { get; private set; }
        public HostAdministratorAuthenticationResult Authenticate(string current, string? account, string? password)
        {
            Calls++;
            return account == "alice" && password == "test-password" ? new(true, "", "test")
                : new(false, "elevation-password-required", "none");
        }
    }
    private sealed class EnvironmentTargets : IHostEnvironmentService
    {
        public SettingsTarget ResolveTarget(ClaimsPrincipal principal, SettingsScope scope) => scope == SettingsScope.HostMachine
            ? new("host/environment/machine", scope) : new("host/environment/user/owned-sid", scope, "owned-sid");
        public void RequireGrant(ClaimsPrincipal principal, SettingsTarget target, HostElevationCapability capability) => throw new NotSupportedException();
        public Task<HostEnvironmentSnapshot> ReadAsync(ClaimsPrincipal principal, SettingsScope scope, CancellationToken ct) => throw new NotSupportedException();
        public Task<PrivilegedEnvironmentState> ReadRawAsync(ClaimsPrincipal principal, SettingsTarget target, CancellationToken ct) => throw new NotSupportedException();
        public Task<PrivilegedOperationResult> ApplyAsync(ClaimsPrincipal principal, SettingsTarget target, EnvironmentChangeSet change, string revision, Guid id, CancellationToken ct) => throw new NotSupportedException();
    }
}
