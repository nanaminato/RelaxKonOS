using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Settings;

internal static class HostSettingsWireChecks
{
    public static async Task RunAsync(string root)
    {
        var provider = new TimeProvider();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new("sub", Guid.NewGuid().ToString()), new("jti", Guid.NewGuid().ToString())], "test"));
        var grants = new HostElevationSessionStore(new TestHostAccountPrivilegeService(), new UploadSessionChecks.SystemMode(), new HostElevationSessionState());
        var journal = new SettingsOperationJournal(new TestHostEnvironment(root), DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root,"wire-keys"))));
        var coordinator = new SettingsOperationCoordinator(journal,provider,grants);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Services.AddAuthorization();
        builder.Services.ConfigureHttpJsonOptions(o => { foreach(var c in RelaxKonOSJsonOptions.Default.Converters) o.SerializerOptions.Converters.Add(c); });
        builder.Services.AddSingleton<IHostTimeService>(provider);
        builder.Services.AddSingleton<IHostElevationSessionStore>(grants);
        builder.Services.AddSingleton(coordinator);
        builder.Services.AddSingleton(journal);
        builder.Services.AddSingleton<IHostEnvironmentService, RefusingEnvironment>();
        builder.Services.AddSingleton<IHostIdentityService, UnusedIdentity>();
        builder.Services.AddSingleton<EnvironmentOperationCoordinator>();
        builder.Services.AddSingleton<HostIdentityOperationCoordinator>();
        builder.Services.AddSingleton<HostNetworkService>(_ => throw new InvalidOperationException("Network not requested"));
        builder.Services.AddSingleton<SettingsCatalog>(_ => throw new InvalidOperationException("Catalog not requested"));
        await using var app = builder.Build();
        app.Use(async (context,next) => { context.User = principal; await next(context); });
        app.UseAuthorization();
        app.MapHostSettingsEndpoints();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        using var time = JsonDocument.Parse(await http.GetStringAsync(SettingsApiRoutes.Time));
        Check(time.RootElement.GetProperty("effectiveState").GetString()=="immediate", "Shared enum converter writes camelCase.");
        Check(time.RootElement.GetProperty("target").GetProperty("scope").GetString()=="hostMachine", "Scope wire changed.");
        Check(time.RootElement.GetProperty("capability").GetProperty("state").GetString()=="elevationRequired", "Grant must remain explicit.");
        var previewRequest = new TimeZonePreviewRequest(provider.Revision,"wire-test",new("Test/Two"));
        using var preview = await http.PostAsync(SettingsApiRoutes.TimePreview, Body(previewRequest));
        preview.EnsureSuccessStatusCode();
        using var planJson = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Check(planJson.RootElement.GetProperty("requiredCapability").GetString()=="hostTimeChange", "Elevation capability wire changed.");
        var id = planJson.RootElement.GetProperty("planId").GetGuid();
        using var denied = await http.PostAsync(SettingsApiRoutes.TimeApply,Body(new SettingsApplyRequest(id)));
        Check((int)denied.StatusCode==428,"Apply without elevation must be refused.");
        using var error = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Check(error.RootElement.GetProperty("problemCode").GetString()=="settings.elevation_required", "Endpoint must name stable problemCode for clients.");
        Check(provider.Writes==0,"Refusal must never mutate host.");
        grants.Grant(principal,HostElevationCapability.HostTimeChange,"host/time",false,"test");
        using var applied = await http.PostAsync(SettingsApiRoutes.TimeApply,Body(new SettingsApplyRequest(id)));
        applied.EnsureSuccessStatusCode();
        using var operation = JsonDocument.Parse(await applied.Content.ReadAsStringAsync());
        Check(operation.RootElement.GetProperty("state").GetString()=="applied", "Operation wire state changed.");
        Check(operation.RootElement.TryGetProperty("problemCode",out var code)&&code.ValueKind==JsonValueKind.Null,"Nullable field must be present.");
        using var replay = await http.PostAsync(SettingsApiRoutes.TimeApply,Body(new SettingsApplyRequest(id)));
        replay.EnsureSuccessStatusCode(); Check(provider.Writes==1,"Original plan cannot replay a provider write.");
        using var envDenied = await http.GetAsync(SettingsApiRoutes.Environment+"?scope=hostMachine");
        Check((int)envDenied.StatusCode==428,"Environment authorization remains explicit.");
        using var envError = JsonDocument.Parse(await envDenied.Content.ReadAsStringAsync());
        Check(envError.RootElement.GetProperty("problemCode").GetString()=="settings.environment.authorization_required","Environment error must use current problemCode extension.");
        var envId = Guid.NewGuid();
        var envTarget = new SettingsTarget("host/environment/machine", SettingsScope.HostMachine);
        var envPlan = new SettingsPlan(envId, envTarget, provider.Revision, DateTimeOffset.UtcNow.AddMinutes(-1),
            [new("X", null, "[configured]")], HostElevationCapability.HostEnvironmentChange, envTarget.ResourceId,
            SettingsEffectiveState.NewLogin, "settings.environment.new_login_required");
        journal.Save(new StoredEnvironmentOperation(principal.FindFirst("sub")!.Value, "test", new([new("X", EnvironmentMutationKind.Set, "test")]),
            new([new("X", EnvironmentMutationKind.Delete)]), envPlan, new(envId, "host.environment", envTarget, SettingsOperationState.Prepared, DateTimeOffset.UtcNow)));
        using var expiredEnv = JsonDocument.Parse(await http.GetStringAsync(SettingsApiRoutes.Operation.Replace("{id}",envId.ToString("D"))));
        Check(expiredEnv.RootElement.GetProperty("state").GetString()=="failed" && expiredEnv.RootElement.GetProperty("problemCode").GetString()=="settings.plan_expired", "Expired environment plans require a server-owned terminal state.");
        using var lateEnvApply = await http.PostAsync(SettingsApiRoutes.EnvironmentApply,Body(new SettingsApplyRequest(envId)));
        lateEnvApply.EnsureSuccessStatusCode();
        using var lateEnv = JsonDocument.Parse(await lateEnvApply.Content.ReadAsStringAsync());
        Check(lateEnv.RootElement.GetProperty("state").GetString()=="failed", "Delayed environment apply cannot access a real provider after expiry closure.");
        Console.WriteLine("Host settings wire checks passed: production routes, camelCase enums, typed problemCode, required nullable fields, elevation before write, same-plan no replay. Controlled provider only.");
    }
    private static StringContent Body<T>(T value) => new(JsonSerializer.Serialize(value,RelaxKonOSJsonOptions.Default),System.Text.Encoding.UTF8,"application/json");
    private static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
    private sealed class TimeProvider : IHostTimeService
    {
        private string zone="Test/One";
        public string Revision => SettingsRevisions.Hash(zone);
        public int Writes {get;private set;}
        public Task<HostTimeState> ReadAsync(CancellationToken ct) => Task.FromResult(new HostTimeState(zone,["Test/One","Test/Two"],Revision,DateTimeOffset.UtcNow,"controlled"));
        public Task<PrivilegedOperationResult> ApplyAsync(TimeZoneChange change,string revision,Guid id,CancellationToken ct)
        { Check(revision==Revision,"Stale provider revision"); Writes++;zone=change.TimeZoneId;return Task.FromResult(new PrivilegedOperationResult(true)); }
    }
    private sealed class UnusedIdentity : IHostIdentityService
    {
        public Task<HostIdentityState> ReadAsync(CancellationToken ct) => throw new InvalidOperationException("No real identity access");
        public Task<PrivilegedOperationResult> ApplyAsync(HostnameChange change, string revision, Guid id, CancellationToken ct) => throw new InvalidOperationException("No real identity write");
    }
    private sealed class RefusingEnvironment : IHostEnvironmentService
    {
        public SettingsTarget ResolveTarget(ClaimsPrincipal p,SettingsScope s) => new("host/environment/machine",SettingsScope.HostMachine);
        public void RequireGrant(ClaimsPrincipal p,SettingsTarget t,HostElevationCapability c) => throw new SettingsException(428,"settings.environment.authorization_required");
        public Task<HostEnvironmentSnapshot> ReadAsync(ClaimsPrincipal p,SettingsScope s,CancellationToken ct) => throw new SettingsException(428,"settings.environment.authorization_required");
        public Task<PrivilegedEnvironmentState> ReadRawAsync(ClaimsPrincipal p,SettingsTarget t,CancellationToken ct) => throw new InvalidOperationException("No real host access");
        public Task<PrivilegedOperationResult> ApplyAsync(ClaimsPrincipal p,SettingsTarget t,EnvironmentChangeSet c,string revision,Guid id,CancellationToken ct) => throw new InvalidOperationException("No real host access");
    }
}
