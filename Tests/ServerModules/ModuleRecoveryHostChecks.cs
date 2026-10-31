using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Server.Startup;

internal static class ModuleRecoveryHostChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", "recovery-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var authority in new[] { "managed", "deployment" })
            {
                var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    { ["ModuleManagement:Authority"] = authority, ["ModuleManagement:Mode"] = "recovery" }).Build();
                try { using var refused = new ModuleManagementStore(invalid, root); throw new Exception("Uninitialized recovery accepted"); }
                catch (InvalidOperationException)
                { check(!File.Exists(Path.Combine(root, "data", "module-management", "state.json")), "recovery refuses uninitialized or deployment-owned authority " + authority); }
            }
            var normal = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ModuleManagement:Authority"] = "managed" }).Build();
            string serviceId;
            Guid oldPlan;
            using (var initial = new ModuleManagementStore(normal, root))
            {
                serviceId = initial.ServiceId;
                var ids = initial.DesiredIds;
                oldPlan = initial.Preview(ModuleTestAuthentication.Admin, new(serviceId, ServerModuleAction.Disable,
                    [ServerModules.GitId], ids.Where(id => id != ServerModules.GitId).ToArray(), initial.Revision, "normal-before-recovery"),
                    ids.Select(id => new ServerModuleDto(id, true, [], [], [], [])).ToArray()).PlanId;
            }
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ModuleManagement:Authority"] = "managed", ["ModuleManagement:Mode"] = "recovery",
                ["ModuleManagement:Administrators:0"] = ModuleTestAuthentication.Admin,
                ["Jwt:Secret"] = new string('r', 48), ["Storage:Provider"] = "memory",
                ["PrivilegedHelper:UserExecutionBackend"] = "disabled",
                ["urls"] = "http://127.0.0.1:0",
                ["Observability:AuditDatabasePath"] = Path.Combine(root, "audit.db"),
                ["Observability:LogDirectory"] = Path.Combine(root, "logs"),
            });
            builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ModuleTestAuthentication>("module-test", _ => { });
            builder.Services.PostConfigure<AuthenticationOptions>(options =>
            { options.DefaultAuthenticateScheme = "module-test"; options.DefaultChallengeScheme = "module-test"; });
            using var store = new ModuleManagementStore(builder.Configuration, root);
            await using var app = await ModuleRecoveryHost.BuildAsync(builder, store);
            var workers = app.Services.GetServices<IHostedService>().Select(service => service.GetType().FullName!).ToArray();
            check(workers.All(name => name.Contains("GenericWebHostService") || name.EndsWith("DataProtectionHostedService") || name.EndsWith("AuthenticationRetentionService")
                || name.EndsWith("RefreshTokenCleanupService") || name.EndsWith("ObservabilityStartupValidationService")), "recovery host registers only transport, audit and authentication workers, no business coordinator: " + string.Join(", ", workers));
            await app.StartAsync();
            var patterns = ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
                .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText).ToHashSet();
            check(patterns.Contains(OwnerDeviceKeyApiRoutes.Challenge) && patterns.Contains(OwnerDeviceKeyApiRoutes.SignIn)
                && !patterns.Contains(OwnerDeviceKeyApiRoutes.Bootstrap) && !patterns.Contains(OwnerDeviceKeyApiRoutes.Invitations),
                "recovery retains existing owner-device authentication while excluding device enrollment and invitations");
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            check((await client.GetAsync("/ready")).StatusCode == HttpStatusCode.ServiceUnavailable,
                "recovery host never advertises candidate readiness");
            check((await client.GetAsync(ServerApiRoutes.Modules)).StatusCode == HttpStatusCode.Unauthorized,
                "recovery inventory retains authentication requirement");
            client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Observer);
            var facts = (await client.GetFromJsonAsync<ServerModuleDto[]>(ServerApiRoutes.Modules, RelaxKonOSJsonOptions.Default))!;
            check(facts.Length == 9 && facts.All(module => !module.EffectiveEnabled && module.ServiceId == serviceId
                && module.RecoveryState == "host-recovery" && module.OrphanedWork.Count > 0 && module.EffectiveRevision != module.DesiredRevision),
                "recovery facts retain stable Server identity and desired intent while effective modules and work stay unverified");
            var request = new ServerModulePlanRequest(serviceId, ServerModuleAction.Enable, facts.Select(module => module.Id).ToArray(),
                facts.Select(module => module.Id).ToArray(), store.Revision, "recovery-all");
            check((await client.PostAsJsonAsync(ServerModuleManagementRoutes.Plans, request, RelaxKonOSJsonOptions.Default)).StatusCode == HttpStatusCode.Forbidden,
                "recovery writes still require explicit module administrator grant");
            client.DefaultRequestHeaders.Remove("test-user"); client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Admin);
            facts = (await client.GetFromJsonAsync<ServerModuleDto[]>(ServerApiRoutes.Modules, RelaxKonOSJsonOptions.Default))!;
            var host = (await client.GetFromJsonAsync<ServerCapabilitiesDto>(ServerApiRoutes.Capabilities, RelaxKonOSJsonOptions.Default))!;
            check(host.Limitations.Contains("module-recovery-mode") && !host.Capabilities.Files && !host.Capabilities.Guardian
                && !host.Capabilities.Docker && !host.Capabilities.PrivilegedOperations,
                "recovery capabilities do not advertise omitted Core or module business routes");
            foreach (var route in new[] { "/api/v1.0/git/repositories", "/api/v1.0/files", "/api/v1.0/installations", "/hubs/terminals", OwnerDeviceKeyApiRoutes.Bootstrap })
                check((await client.PostAsync(route, null)).StatusCode == HttpStatusCode.NotFound, "recovery host omits business or enrollment route " + route);
            var denied = store.Preview(ModuleTestAuthentication.Admin, request with
                { Action = ServerModuleAction.Disable, EnabledModuleIds = [], IdempotencyKey = "deny-disable" }, facts);
            check(denied.BlockedBy.Any(reason => reason.ProblemCode == "module-recovery-mode"),
                "recovery mode refuses disabling or omitting compiled members");
            try { store.Apply(ModuleTestAuthentication.Admin, oldPlan, facts); throw new Exception("Normal disable plan escaped recovery restrictions"); }
            catch (ModuleManagementException exception) when (exception.Code == "module-recovery-mode")
            { check(true, "recovery revalidates an older normal-mode plan instead of executing its previously allowed disable"); }
            check(facts.All(module => module.AllowedActions!.Count(action => action.Allowed) == 1
                && module.AllowedActions!.Single(action => action.Allowed).Action == ServerModuleAction.Enable),
                "recovery action admissions expose only the current full-set enable action");
            try { store.VerifyStartup(store.DesiredIds); throw new Exception("Recovery verified as normal startup"); }
            catch (ModuleManagementException exception) when (exception.Code == "module-recovery-mode")
            { check(true, "recovery process cannot verify desired intent as a healthy effective set"); }
            var response = await client.PostAsJsonAsync(ServerModuleManagementRoutes.Plans, request, RelaxKonOSJsonOptions.Default);
            response.EnsureSuccessStatusCode();
            var plan = (await response.Content.ReadFromJsonAsync<ServerModulePlanDto>(RelaxKonOSJsonOptions.Default))!;
            check(plan.BlockedBy.Count == 0 && plan.RestartRequired, "recovery freezes the exact full compiled target and requires a normal restart");
            var applied = await client.PostAsync(ServerModuleManagementRoutes.Apply.Replace("{planId:guid}", plan.PlanId.ToString()), null);
            applied.EnsureSuccessStatusCode();
            var operation = (await applied.Content.ReadFromJsonAsync<ServerModuleOperationDto>(RelaxKonOSJsonOptions.Default))!;
            check(operation.State == ServerModuleOperationState.AwaitingRestart && operation.EnabledModuleIds.Count == 9,
                "authenticated recovery apply persists the normal ledger receipt without claiming completion");
            await app.StopAsync();
        }
        finally { Directory.Delete(root, true); }
    }
}
