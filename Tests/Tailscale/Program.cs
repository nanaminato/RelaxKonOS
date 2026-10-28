using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;
using System.Text.Json;
using RelaxKonOS.Server.Tailscale;
using RelaxKonOS.Protocol.Tailscale;

static void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
using var document = JsonDocument.Parse("""
{"BackendState":"Running","Version":"1.90.0","AuthURL":"secret","PrivateKey":"secret",
"Self":{"ID":"self","HostName":"host","TailscaleIPs":["100.64.0.1"],"Online":true},
"Peer":{"b":{"ID":"b","HostName":"peer","Online":false},"a":{"ID":"a","Online":true,"ExitNode":true}}}
""");
var status = TailscaleManager.ParseStatus(document.RootElement);
Check(status.State == "observed" && status.BackendState == "Running");
Check(status.Self!.Addresses.Single() == "100.64.0.1");
Check(status.Peers!.Count == 2 && status.Peers[0].Id == "a" && status.Peers[0].ExitNode);
Check(!JsonSerializer.Serialize(status).Contains("secret"));
using var invalid = JsonDocument.Parse("{}");
Check(TailscaleManager.ParseStatus(invalid.RootElement).State == "invalid-response");
Check(TailscaleEndpoints.Status == "/api/v1.0/tailscale/status");
Console.WriteLine("6 Tailscale contract/parser checks passed.");

var builder = WebApplication.CreateBuilder();
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ModuleResidency:IdleGraceSeconds"] = "0"
});
var module = new RelaxKonOS.Server.Startup.Modules.TailscaleModule();
Check(module is IServerModuleResidency);
var ids = new HashSet<string> { module.Id };
var options = ModuleResidencyOptions.Parse(builder.Configuration, ids, ids);
builder.Services.AddSingleton<RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport>(new StubTransport());
using var root = builder.Services.BuildServiceProvider();
await using var coordinator = new ModuleResidencyCoordinator([module.RuntimeFactory], options, root,
    ServerModuleContext.Create(builder.Configuration, builder.Environment, "Sqlite"),
    NullLogger<ModuleResidencyCoordinator>.Instance);
Check(coordinator.Describe(module.Id)!.State == ModuleResidencyState.Dormant);
ITailscaleManager previous;
using (var lease = await coordinator.AcquireAsync(module.Id))
{
    previous = (ITailscaleManager)lease.Runtime.Resolve(typeof(ITailscaleManager));
    await coordinator.SweepAsync();
    Check(coordinator.Describe(module.Id)!.State == ModuleResidencyState.Active);
    Check(coordinator.Describe(module.Id)!.ActiveLeases == 1);
}
await coordinator.SweepAsync();
Check(coordinator.Describe(module.Id)!.State == ModuleResidencyState.Dormant);
using (var lease = await coordinator.AcquireAsync(module.Id))
    Check(!ReferenceEquals(previous, lease.Runtime.Resolve(typeof(ITailscaleManager))));
using var cancellation = new CancellationTokenSource();
cancellation.Cancel();
try
{
    await module.RuntimeFactory.CreateAsync(root,
        ServerModuleContext.Create(builder.Configuration, builder.Environment, "Sqlite"), cancellation.Token);
    throw new Exception("Cancelled runtime creation was accepted");
}
catch (OperationCanceledException) { }
Console.WriteLine("7 Tailscale residency checks passed.");

// The production module graph resolves scoped authorization through a short-lived scope.
var residencyFolder = Directory.CreateTempSubdirectory("relaxkonos-tailscale-residency-");
try
{
    var residentBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = residencyFolder.FullName });
    residentBuilder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
    residentBuilder.Services.AddSingleton<RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport>(new StubTransport());
    residentBuilder.Services.AddScoped<RelaxKonOS.Server.Privileged.IHostElevationSessionStore>(_ => new StubGrants { Allowed = true });
    var residentContext = ServerModuleContext.Create(residentBuilder.Configuration, residentBuilder.Environment, "Sqlite");
    module.ConfigureServices(residentBuilder, residentContext);
    residentBuilder.Services.AddSingleton(services => new ModuleResidencyCoordinator([module.RuntimeFactory], options, services,
        residentContext, NullLogger<ModuleResidencyCoordinator>.Instance));
    await using var residentRoot = residentBuilder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    var residentCoordinator = residentRoot.GetRequiredService<ModuleResidencyCoordinator>();
    var controlProxy = residentRoot.GetRequiredService<ITailscaleControl>();
    Check(residentCoordinator.Describe(module.Id)!.State == ModuleResidencyState.Dormant);
    try { await controlProxy.ReadAsync(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
        [new("sub", Guid.NewGuid().ToString())], "test")), default); throw new Exception("Missing Helper was accepted"); }
    catch (TailscaleControlException ex) { Check(ex.ProblemCode == "tailscale.control_unavailable"); }
    await residentCoordinator.SweepAsync();
    Check(residentCoordinator.Describe(module.Id)!.State == ModuleResidencyState.Dormant);
}
finally { residencyFolder.Delete(true); }
Console.WriteLine("3 Tailscale production control residency/scope checks passed.");

// Command outcomes never expose native diagnostics or malformed JSON.
foreach (var state in new[] { "not-installed", "unavailable", "timeout", "unsupported" })
{
    var observed = await new TailscaleManager(new StubStatusCommand(new(state, "private diagnostic"))).GetStatusAsync(default);
    Check(observed.State == state && observed.Version is null && observed.Peers is null && observed.ProblemCode is not null);
}
foreach (var json in new[] { "null", "[]", "{", "{\"BackendState\":false}" })
{
    var observed = await new TailscaleManager(new StubStatusCommand(new("observed", json))).GetStatusAsync(default);
    Check(observed.State == "invalid-response" && observed.ProblemCode == "tailscale.invalid_response");
}
using (var limit = new CancellationTokenSource())
{
    var bytes = await TailscaleStatusCommand.ReadBoundedAsync(new MemoryStream(new byte[1024 * 1024]), limit);
    Check(bytes.Length == 1024 * 1024 && !limit.IsCancellationRequested);
}
using (var limit = new CancellationTokenSource())
{
    try
    {
        await TailscaleStatusCommand.ReadBoundedAsync(new MemoryStream(new byte[1024 * 1024 + 1]), limit);
        throw new Exception("Oversized output accepted");
    }
    catch (IOException) { Check(limit.IsCancellationRequested); }
}
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { await new TailscaleStatusCommand().ReadAsync(cancelled.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { }
}
Console.WriteLine("11 Tailscale command/failure checks passed.");

// Verify the exact IPC shape before the injected command is allowed to run.
var helperRequest = new RelaxKonOS.Protocol.Privileged.PrivilegedOperationRequest(
    RelaxKonOS.Protocol.Privileged.PrivilegedOperationKind.TailscaleStatus,
    OperationId: Guid.NewGuid(), Correlation: RelaxKonOS.Protocol.Observability.CorrelationContext.Create(Guid.NewGuid(), "privileged.operation"));
var helperCommand = new StubStatusCommand(new("observed", document.RootElement.GetRawText()));
var helperResult = await RelaxKonOS.PrivilegedHelper.TailscaleOperations.ExecuteAsync(helperRequest, helperCommand);
Check(helperResult.Success && helperResult.TailscaleStatus?.Peers?.Count == 2 && helperCommand.Calls == 1);
Check(!JsonSerializer.Serialize(helperResult).Contains("secret"));
foreach (var malformed in new[] {
    helperRequest with { Path = "arbitrary" }, helperRequest with { ExpectedRevision = "unexpected" },
    helperRequest with { OperationId = Guid.Empty }, helperRequest with { Correlation = null },
    helperRequest with { Version = "old" }, helperRequest with { Operation = RelaxKonOS.Protocol.Privileged.PrivilegedOperationKind.HostNetworkRead } })
{
    var rejected = await RelaxKonOS.PrivilegedHelper.TailscaleOperations.ExecuteAsync(malformed, helperCommand);
    Check(!rejected.Success && rejected.ProblemCode == RelaxKonOS.Protocol.Privileged.PrivilegedProblemCode.InvalidRequest);
}
Check(helperCommand.Calls == 1);
var transport = new StubTransport { Result = helperResult };
var manager = new HelperTailscaleManager(transport);
Check((await manager.GetStatusAsync(default)).Peers?.Count == 2);
Check(transport.Request?.Operation == RelaxKonOS.Protocol.Privileged.PrivilegedOperationKind.TailscaleStatus
    && transport.Request.OperationId != Guid.Empty && transport.Request.Path is null);
transport.Result = new(false, Error: "private diagnostic");
var failed = await manager.GetStatusAsync(default);
Check(failed.State == "unavailable" && !JsonSerializer.Serialize(failed).Contains("private"));
transport.Result = new(true, TailscaleStatus: status, Version: "old");
Check((await manager.GetStatusAsync(default)).State == "unavailable");
transport.Result = new(true);
Check((await manager.GetStatusAsync(default)).State == "unavailable");
Console.WriteLine("14 Tailscale Helper boundary checks passed.");

// Exercise the real mapped route and authorization middleware over loopback HTTP.
await TailscaleControlChecks.RunAsync();
await TailscaleHelperControlChecks.RunAsync();
await TailscaleHelperBridgeChecks.RunAsync();
await TailscaleRecoveryChecks.RunAsync();
await TailscaleQuiescenceChecks.RunAsync();
var httpFolder = Directory.CreateTempSubdirectory("relaxkonos-tailscale-http-");
var webBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = httpFolder.FullName });
webBuilder.Logging.ClearProviders();
webBuilder.Host.UseDefaultServiceProvider(options => { options.ValidateScopes = true; options.ValidateOnBuild = true; });
webBuilder.WebHost.UseUrls("http://127.0.0.1:0");
webBuilder.Services.AddAuthentication("test").AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
webBuilder.Services.AddAuthorization();
webBuilder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
var grants = new StubGrants();
webBuilder.Services.AddScoped<RelaxKonOS.Server.Privileged.IHostElevationSessionStore>(_ => grants);
webBuilder.Services.AddSingleton<ITailscaleControlAuthorization, TailscaleControlAuthorization>();
var controlHost = new HttpControlHost();
webBuilder.Services.AddSingleton<ITailscaleControl>(services => new TailscaleOperationCoordinator(
    new TailscaleOperationJournal(webBuilder.Environment, new EphemeralDataProtectionProvider()), controlHost, services.GetRequiredService<ITailscaleControlAuthorization>()));
webBuilder.Services.AddSingleton<ITailscaleManager>(new TailscaleManager(new StubStatusCommand(new("observed", document.RootElement.GetRawText()))));
await using (var web = webBuilder.Build())
{
    web.UseAuthentication();
    web.UseAuthorization();
    module.MapEndpoints(web, ServerModuleContext.Create(webBuilder.Configuration, webBuilder.Environment, "Sqlite"));
    await web.StartAsync();
    using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(web.Urls.Single()) };
    Check((await http.GetAsync(TailscaleEndpoints.Status)).StatusCode == System.Net.HttpStatusCode.Unauthorized);
    http.DefaultRequestHeaders.Authorization = new("test");
    var restricted = JsonSerializer.Deserialize<TailscaleStatusDto>(await http.GetStringAsync(TailscaleEndpoints.Status), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    Check(restricted.PeersRestricted && restricted.Peers is null && restricted.Self?.Id == "self");
    Check((await http.GetAsync(TailscaleEndpoints.Control)).StatusCode == System.Net.HttpStatusCode.Forbidden);
    grants.Allowed = true;
    var allowed = JsonSerializer.Deserialize<TailscaleStatusDto>(await http.GetStringAsync(TailscaleEndpoints.Status), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    Check(!allowed.PeersRestricted && allowed.Peers?.Count == 2);
    Check(grants.Target == TailscaleAccess.Resource && grants.Capability == RelaxKonOS.Protocol.Privileged.HostElevationCapability.HostNetworkChange);
    var controlSnapshot = JsonSerializer.Deserialize<TailscaleControlSnapshot>(await http.GetStringAsync(TailscaleEndpoints.Control), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    Check(controlSnapshot.Revision == controlHost.Snapshot.Revision);
    var operationId = Guid.NewGuid();
    var planRequest = new TailscalePrepareRequest(operationId, controlSnapshot.Revision, TailscaleAction.Connect);
    using var planResponse = await http.PostAsJsonAsync(TailscaleEndpoints.Operations, planRequest);
    Check(planResponse.IsSuccessStatusCode && controlHost.Executions == 0);
    var operationPath = TailscaleEndpoints.Operations + "/" + operationId.ToString("D");
    using var deniedConfirmation = await http.PostAsJsonAsync(operationPath + "/execute", new TailscaleExecuteRequest(false));
    Check(deniedConfirmation.StatusCode == System.Net.HttpStatusCode.BadRequest && controlHost.Executions == 0);
    using var appliedResponse = await http.PostAsJsonAsync(operationPath + "/execute", new TailscaleExecuteRequest(true));
    Check(appliedResponse.IsSuccessStatusCode && controlHost.Executions == 1);
    var storedOperation = JsonSerializer.Deserialize<TailscaleOperation>(await http.GetStringAsync(operationPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    Check(storedOperation.State == TailscaleOperationState.Applied && storedOperation.OperationId == operationId);
    using var repeated = await http.PostAsJsonAsync(operationPath + "/execute", new TailscaleExecuteRequest(true));
    Check(repeated.IsSuccessStatusCode && controlHost.Executions == 1);
    controlHost.FailExecute = true;
    var unresolvedId = Guid.NewGuid();
    var unresolvedPath = TailscaleEndpoints.Operations + "/" + unresolvedId.ToString("D");
    using var unresolvedPlan = await http.PostAsJsonAsync(TailscaleEndpoints.Operations,
        new TailscalePrepareRequest(unresolvedId, controlHost.Snapshot.Revision, TailscaleAction.Disconnect));
    using var unresolvedResponse = await http.PostAsJsonAsync(unresolvedPath + "/execute", new TailscaleExecuteRequest(true));
    Check(unresolvedPlan.IsSuccessStatusCode && unresolvedResponse.IsSuccessStatusCode && controlHost.Executions == 2);
    http.DefaultRequestHeaders.Authorization = null;
    Check((await http.GetAsync(unresolvedPath + "/recovery")).StatusCode == System.Net.HttpStatusCode.Unauthorized);
    http.DefaultRequestHeaders.Authorization = new("test");
    grants.Allowed = false;
    Check((await http.GetAsync(unresolvedPath + "/recovery")).StatusCode == System.Net.HttpStatusCode.Forbidden);
    grants.Allowed = true;
    var recovery = JsonSerializer.Deserialize<TailscaleRecoverySnapshot>(await http.GetStringAsync(unresolvedPath + "/recovery"), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    Check(recovery.OperationId == unresolvedId && recovery.CanClose && recovery.ExpiresAt > DateTimeOffset.UtcNow);
    var closeRequest = new TailscaleCloseRequest(unresolvedId, recovery.Observation!.Revision, true);
    using var unconfirmedClose = await http.PostAsJsonAsync(unresolvedPath + "/recovery/close", closeRequest with { Confirmed = false });
    Check(unconfirmedClose.StatusCode == System.Net.HttpStatusCode.BadRequest && controlHost.Closures == 0);
    using var wrongIdentityClose = await http.PostAsJsonAsync(operationPath + "/recovery/close", closeRequest);
    Check(wrongIdentityClose.StatusCode == System.Net.HttpStatusCode.BadRequest && controlHost.Closures == 0);
    using var closeResponse = await http.PostAsJsonAsync(unresolvedPath + "/recovery/close", closeRequest);
    Check(closeResponse.IsSuccessStatusCode && controlHost.Closures == 1);
    var closedOperation = JsonSerializer.Deserialize<TailscaleOperation>(await http.GetStringAsync(unresolvedPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    Check(closedOperation.State == TailscaleOperationState.Unknown && closedOperation.Closure?.OperationId == unresolvedId
        && closedOperation.Closure.Actor == "b192be55-f4ea-4277-9b98-5c95b3be23af");
    using var repeatClose = await http.PostAsJsonAsync(unresolvedPath + "/recovery/close", closeRequest);
    Check(repeatClose.IsSuccessStatusCode && controlHost.Closures == 1 && controlHost.Executions == 2);
    await web.StopAsync();
}
httpFolder.Delete(true);
Console.WriteLine("20 Tailscale HTTP authentication/control/recovery checks passed.");

sealed class StubStatusCommand(TailscaleCommandResult result) : ITailscaleStatusCommand
{
    public int Calls { get; private set; }
    public Task<TailscaleCommandResult> ReadAsync(CancellationToken cancellationToken) { Calls++; return Task.FromResult(result); }
}

sealed class StubTransport : RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport
{
    public RelaxKonOS.Protocol.Privileged.PrivilegedOperationResult Result { get; set; } = new(false);
    public RelaxKonOS.Protocol.Privileged.PrivilegedOperationRequest? Request { get; private set; }
    public Task<RelaxKonOS.Protocol.Privileged.PrivilegedOperationResult> ExecuteAsync(
        RelaxKonOS.Protocol.Privileged.PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
    { Request = request; return Task.FromResult(Result); }
}

sealed class StubGrants : RelaxKonOS.Server.Privileged.IHostElevationSessionStore, ITailscaleControlAuthorization
{
    public bool IsGranted(System.Security.Claims.ClaimsPrincipal principal) => IsGranted(principal, RelaxKonOS.Protocol.Privileged.HostElevationCapability.HostNetworkChange, TailscaleAccess.Resource);
    public bool Allowed { get; set; }
    public string? Target { get; private set; }
    public RelaxKonOS.Protocol.Privileged.HostElevationCapability Capability { get; private set; }
    public bool IsGranted(System.Security.Claims.ClaimsPrincipal principal, RelaxKonOS.Protocol.Privileged.HostElevationCapability capability, string target)
    { Target = target; Capability = capability; return Allowed && target == TailscaleAccess.Resource && capability == RelaxKonOS.Protocol.Privileged.HostElevationCapability.HostNetworkChange; }
    public DateTimeOffset Grant(System.Security.Claims.ClaimsPrincipal principal, RelaxKonOS.Protocol.Privileged.HostElevationCapability capability, string target, bool includeDescendants, string authenticationMethod, string? correlationId = null) => throw new NotSupportedException();
    public void Revoke(System.Security.Claims.ClaimsPrincipal principal) { }
}

sealed class TestAuthenticationHandler(Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
    Microsoft.Extensions.Logging.ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
    : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.Authorization != "test") return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new("sub", "b192be55-f4ea-4277-9b98-5c95b3be23af")], "test"));
        return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.Success(new(principal, "test")));
    }
}




sealed class HttpControlHost : ITailscaleControlHost
{
    public TailscaleControlSnapshot Snapshot { get; private set; } = new(new('D', 64), false, true, new(true, false), true);
    public int Executions { get; private set; }
    public int Closures { get; private set; }
    public bool FailExecute { get; set; }
    private readonly Dictionary<Guid, TailscaleRecoveryClosure> _closures = new();
    public Task<TailscaleRecoverySnapshot> ReadRecoveryAsync(Guid operationId, CancellationToken cancellationToken) => Task.FromResult(new TailscaleRecoverySnapshot(operationId, Snapshot, true));
    public Task<TailscaleRecoveryClosure> CloseAsync(TailscaleCloseRequest request, string actor, CancellationToken cancellationToken)
    {
        Closures++; var closure = new TailscaleRecoveryClosure(request.OperationId, Snapshot, DateTimeOffset.UtcNow, actor);
        _closures[request.OperationId] = closure; return Task.FromResult(closure);
    }
    public Task<TailscaleRecoveryClosure?> GetClosureAsync(Guid operationId, CancellationToken cancellationToken) => Task.FromResult(_closures.GetValueOrDefault(operationId));
    public Task<TailscaleControlSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);
    public Task<TailscaleControlResult?> GetResultAsync(Guid operationId, CancellationToken cancellationToken) => Task.FromResult<TailscaleControlResult?>(null);
    public Task<TailscaleControlResult> ExecuteAsync(TailscalePrepareRequest request, CancellationToken cancellationToken)
    {
        Executions++;
        if (FailExecute) return Task.FromResult(new TailscaleControlResult(request.OperationId, false));
        Snapshot = Snapshot with { Connected = true, Revision = new('E', 64) };
        return Task.FromResult(new TailscaleControlResult(request.OperationId, true, Snapshot));
    }
}


