using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Startup;

internal static class ModuleManagementHttpChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", "management-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ModuleManagement:Authority"] = "managed",
                ["ModuleManagement:Administrators:0"] = ModuleTestAuthentication.Admin,
            });
            using var store = new ModuleManagementStore(builder.Configuration, root);
            var modules = ServerModules.CreateManaged(store.DesiredIds);
            builder.Services.AddSingleton(store);
            builder.Services.AddSingleton<ModuleManagementExecutor>();
            builder.Services.AddHostedService(services => services.GetRequiredService<ModuleManagementExecutor>());
            builder.Services.AddSingleton(modules);
            builder.Services.AddSingleton(ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
            builder.Services.AddAuthentication("module-test").AddScheme<AuthenticationSchemeOptions, ModuleTestAuthentication>("module-test", _ => { });
            builder.Services.AddAuthorization(options => options.AddPolicy(ServerModuleManagementEndpoints.ManagePolicy,
                policy => policy.RequireAuthenticatedUser().RequireAssertion(context => ServerModuleManagementEndpoints.CanManage(context.User, builder.Configuration))));
            await using var app = builder.Build();
            app.UseAuthentication(); app.UseAuthorization();
            app.MapServerModuleManagementEndpoints();
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var detail = ServerModuleManagementRoutes.Detail.Replace("{moduleId}", ServerModules.GitId);
            check((await client.GetAsync(detail)).StatusCode == HttpStatusCode.Unauthorized, "module detail rejects unauthenticated requests");
            client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Observer);
            var observer = await client.GetFromJsonAsync<ServerModuleDto>(detail, RelaxKonOSJsonOptions.Default);
            check(observer?.AllowedActions?.All(x => !x.Allowed && x.BlockedBy.Any(reason => reason.ProblemCode == "module-forbidden")) == true,
                "ordinary module readers receive explicit forbidden action admissions");
            var target = store.DesiredIds.Where(id => id != ServerModules.GitId).Order(StringComparer.Ordinal).ToArray();
            var request = new ServerModulePlanRequest(store.ServiceId, ServerModuleAction.Disable, [ServerModules.GitId], target, store.Revision, "http-one");
            check((await client.PostAsJsonAsync(ServerModuleManagementRoutes.Plans, request)).StatusCode == HttpStatusCode.Forbidden,
                "global module changes require explicit server administration grant");
            client.DefaultRequestHeaders.Remove("test-user"); client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Admin);
            var response = await client.PostAsJsonAsync(ServerModuleManagementRoutes.Plans, request, RelaxKonOSJsonOptions.Default);
            response.EnsureSuccessStatusCode();
            var plan = (await response.Content.ReadFromJsonAsync<ServerModulePlanDto>(RelaxKonOSJsonOptions.Default))!;
            check(plan.BlockedBy.Count == 0, "HTTP module preview binds a valid exact set");
            var apply = ServerModuleManagementRoutes.Apply.Replace("{planId:guid}", plan.PlanId.ToString());
            var applied = await client.PostAsync(apply, null); applied.EnsureSuccessStatusCode();
            var operation = (await applied.Content.ReadFromJsonAsync<ServerModuleOperationDto>(RelaxKonOSJsonOptions.Default))!;
            var current = (await client.GetFromJsonAsync<ServerModuleDto>(detail, RelaxKonOSJsonOptions.Default))!;
            check(operation.State == ServerModuleOperationState.AwaitingRestart && current.EffectiveEnabled && !current.DesiredEnabled
                && current.RestartRequired && current.PendingOperationId == operation.OperationId,
                "HTTP apply retains effective routes and reports pending restart with original operation");
            var receipt = await client.GetFromJsonAsync<ServerModuleOperationDto>(ServerModuleManagementRoutes.Operation.Replace("{operationId:guid}", operation.OperationId.ToString()), RelaxKonOSJsonOptions.Default);
            check(receipt?.OperationId == operation.OperationId, "HTTP operation query returns the durable original receipt");
            var status = await client.GetFromJsonAsync<ServerModulePlanStatusDto>(ServerModuleManagementRoutes.Plan.Replace("{planId:guid}", plan.PlanId.ToString()), RelaxKonOSJsonOptions.Default);
            check(status?.Operation?.OperationId == operation.OperationId, "HTTP original plan lookup recovers receipt without resubmitting changes");
            var policyRequest = new ServerModulePlanRequest(store.ServiceId, ServerModuleAction.SetResidencyPolicy,
                [ServerModules.GitId], target, store.Revision, "http-policy", Strategy: "KeepLoaded", IdleGraceSeconds: 42);
            var policyResponse = await client.PostAsJsonAsync(ServerModuleManagementRoutes.Plans, policyRequest, RelaxKonOSJsonOptions.Default);
            policyResponse.EnsureSuccessStatusCode();
            var policyPlan = (await policyResponse.Content.ReadFromJsonAsync<ServerModulePlanDto>(RelaxKonOSJsonOptions.Default))!;
            var policyApplied = await client.PostAsync(ServerModuleManagementRoutes.Apply.Replace("{planId:guid}", policyPlan.PlanId.ToString()), null);
            policyApplied.EnsureSuccessStatusCode();
            var policyFact = (await client.GetFromJsonAsync<ServerModuleDto>(detail, RelaxKonOSJsonOptions.Default))!;
            check(policyFact.DesiredResidencyPolicy == new ServerModuleResidencyPolicyDto("KeepLoaded", 42)
                && policyFact.EffectiveResidencyPolicy == new ServerModuleResidencyPolicyDto("IdleDelayed", 300) && policyFact.RestartRequired,
                "HTTP policy apply exposes desired and effective strategies independently while awaiting restart");
            await app.StopAsync();
        }
        finally { Directory.Delete(root, true); }
    }
}

internal sealed class ModuleTestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string Admin = "10000000-0000-0000-0000-000000000001";
    internal const string Observer = "10000000-0000-0000-0000-000000000002";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var id = Request.Headers["test-user"].ToString();
        if (id != Admin && id != Observer) return Task.FromResult(AuthenticateResult.NoResult());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", id)], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
