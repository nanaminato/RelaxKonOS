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

public static class HostElevationWireChecks
{
    public static async Task RunAsync()
    {
        var privileges = new TestHostAccountPrivilegeService { Level = HostAccountPrivilege.HostAdministrator };
        var mode = new UploadSessionChecks.SystemMode();
        var store = new HostElevationSessionStore(privileges, mode);
        var administrator = new Administrator();
        var method = "system";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IServerModeResolver>(mode);
        builder.Services.AddSingleton<IHostElevationSessionStore>(store);
        builder.Services.AddSingleton<IHostAdministratorAuthenticator>(administrator);
        builder.Services.AddSingleton<IHostEnvironmentService, EnvironmentTargets>();
        builder.Services.AddSingleton<OwnerDeviceKeyService>();
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
        app.UseAuthorization(); app.MapPrivilegedEndpoints(); await app.StartAsync();
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
        Console.WriteLine("Host elevation HTTP checks passed: administrator revalidation, alias challenge, exact grants and target ownership.");
    }

    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
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
        public Task<HostEnvironmentSnapshot> ReadAsync(ClaimsPrincipal principal, SettingsScope scope, bool reveal, CancellationToken ct) => throw new NotSupportedException();
        public Task<PrivilegedEnvironmentState> ReadRawAsync(ClaimsPrincipal principal, SettingsTarget target, CancellationToken ct) => throw new NotSupportedException();
        public Task<PrivilegedOperationResult> ApplyAsync(ClaimsPrincipal principal, SettingsTarget target, EnvironmentChangeSet change, string revision, Guid id, CancellationToken ct) => throw new NotSupportedException();
    }
}
