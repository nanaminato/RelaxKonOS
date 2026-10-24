using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.TaskOrchestration;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Protocol.Privileged;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Server.ProxyServer;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class TaskPlanningHttpChecks
{
    public static async Task<int> RunAsync(TaskPlanIntent intent)
    {
        var count = 0;
        void Check(bool condition, string label)
        { if (!condition) throw new Exception(label); count++; Console.WriteLine("PASS: HTTP " + label); }
        var service = new PreviewService();
        var mode = new HostModeFixture();
        var changeService = new ChangeService();
        var transport = new ReadTransport();
        var production = new RuntimeTaskPreviewService(new SingBoxRuntimeService(transport, null!, null!, mode, null!),
            new RuntimeCatalogService(transport, null!));
        var productionPlan = await production.PreviewAsync("production-subject", intent, default);
        Check(productionPlan.Semantics.CanonicalSubject == "production-subject"
            && productionPlan.Semantics.HostId == "host"
            && transport.Commands.SequenceEqual([PrivilegedOperationKind.SingBoxRuntimeQuery, PrivilegedOperationKind.RuntimeCatalogGet]),
            "Production preview composes only the real domain read services and caches the request installation snapshot");
        var beforeInvalid = transport.Commands.Count;
        try { await production.PreviewAsync("subject", intent with { Steps = [intent.Steps[0] with { ActionId = "shell.run" }] }, default); throw new Exception("Invalid production intent accepted"); }
        catch (TaskPlanningException)
        { Check(transport.Commands.Count == beforeInvalid, "Production adapter rejects unknown actions before even reading host identity"); }
        var changeTransport = new ChangeTransport();
        var runtimeStore = new ProxyServerStore(null!, new EphemeralDataProtectionProvider());
        var productionChanges = new RuntimeTaskChangePlanningService(
            new SingBoxRuntimeService(changeTransport, runtimeStore, null!, mode, null!), new RuntimeCatalogService(changeTransport, null!));
        var productionChangePlan = await productionChanges.PlanAsync("production-subject", RuntimeChangePlanningChecks.Intent(RuntimeChange.Update), default);
        Check(productionChangePlan.Semantics.Steps.Single().Disposition == TaskStepDisposition.Ready
            && changeTransport.Commands.SequenceEqual([PrivilegedOperationKind.SingBoxRuntimeQuery, PrivilegedOperationKind.SingBoxRuntimePlan,
                PrivilegedOperationKind.SingBoxRuntimeQuery]),
            "Production change planning queries, freezes and rechecks through the domain without execution or elevation");
        changeTransport.DriftOnPlan = true;
        try { await productionChanges.PlanAsync("production-subject", RuntimeChangePlanningChecks.Intent(RuntimeChange.Update), default); throw new Exception("Accepted planning drift"); }
        catch (TaskPlanningException ex)
        { Check(ex.ProblemCode == "task.observation_changed", "Published-configuration drift during domain planning cannot produce a frozen proposal"); }
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, Authentication>("fixture", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(RuntimeCatalogEndpoints.ReadPolicy,
                policy => policy.RequireAuthenticatedUser().RequireClaim("role", "observer", "controller"));
            options.AddPolicy(RuntimeCatalogEndpoints.ManagePolicy,
                policy => policy.RequireAuthenticatedUser().RequireClaim("role", "controller"));
        });
        builder.Services.AddSingleton<IRuntimeTaskPreviewService>(service);
        builder.Services.AddSingleton<IRuntimeTaskChangePlanningService>(changeService);
        builder.Services.AddSingleton<IServerModeResolver>(mode);
        var planRoot = Path.Combine(Path.GetTempPath(), "task-plan-http-" + Guid.NewGuid().ToString("N"));
        builder.Services.AddSingleton(new TaskPlanStore(Path.Combine(planRoot, "plans.db"), new EphemeralDataProtectionProvider()));
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.MapTaskPlanningEndpoints();
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new(app.Urls.Single()) };
            ByteArrayContent Body()
            {
                var content = new ByteArrayContent(TaskPlanJson.WriteIntent(intent));
                content.Headers.ContentType = new("application/json"); return content;
            }
            using (var response = await http.PostAsync(TaskPlanningRoutes.Preview, Body()))
                Check(response.StatusCode == HttpStatusCode.Unauthorized && service.Calls == 0, "Anonymous preview cannot query domain observations");
            http.DefaultRequestHeaders.Authorization = new("Bearer", "denied");
            using (var response = await http.PostAsync(TaskPlanningRoutes.Preview, Body()))
                Check(response.StatusCode == HttpStatusCode.Forbidden && service.Calls == 0, "A user without runtime read permission cannot preview");
            http.DefaultRequestHeaders.Authorization = new("Bearer", "no-subject");
            using (var response = await http.PostAsync(TaskPlanningRoutes.Preview, Body()))
                Check(response.StatusCode == HttpStatusCode.Forbidden && service.Calls == 0, "Read permission without a canonical subject is refused");
            http.DefaultRequestHeaders.Authorization = new("Bearer", "read");
            mode.Enabled = false;
            using (var response = await http.PostAsync(TaskPlanningRoutes.Preview, Body()))
                Check(response.StatusCode == HttpStatusCode.Forbidden && service.Calls == 0, "Host feature policy blocks preview before domain access");
            mode.Enabled = true;
            using (var response = await http.PostAsync(TaskPlanningRoutes.Preview, new StringContent("{}")))
                Check(response.StatusCode == HttpStatusCode.UnsupportedMediaType && service.Calls == 0, "Non-JSON requests are rejected");
            foreach (var body in new[] { "{\"sourceKind\":\"app\",\"sourceKind\":\"recipe\"}", "{\"canonicalSubject\":\"forged\"}", "null" })
            {
                using var response = await http.PostAsync(TaskPlanningRoutes.Preview, new StringContent(body, Encoding.UTF8, "application/json"));
                Check(response.StatusCode == HttpStatusCode.BadRequest && service.Calls == 0, "Duplicate, injected identity and null input are refused before preview");
            }
            var oversized = new string('x', TaskPlanLimits.Default.MaximumPlanBytes + 1);
            using (var response = await http.PostAsync(TaskPlanningRoutes.Preview, new StringContent(oversized, Encoding.UTF8, "application/json")))
                Check(response.StatusCode == HttpStatusCode.RequestEntityTooLarge && service.Calls == 0, "Declared oversized input is bounded");
            using (var response = await http.PostAsync(TaskPlanningRoutes.Preview, new Chunked(Encoding.UTF8.GetBytes(oversized))))
                Check(response.StatusCode == HttpStatusCode.RequestEntityTooLarge && service.Calls == 0, "Chunked oversized input is bounded without a Content-Length");
            Guid planId = default;
            string planDigest = "";
            using (var response = await http.PostAsync(TaskPlanningRoutes.Preview, Body()))
            {
                var plan = await response.Content.ReadFromJsonAsync<TaskPlan>(RelaxKonOSJsonOptions.Default);
                Check(response.IsSuccessStatusCode && service.Calls == 1 && plan is not null
                    && plan.Semantics.CanonicalSubject == Authentication.Subject && plan.Semantics.HostId == "host"
                    && plan.PlanDigest == TaskPlanCanonicalizer.Digest(plan.Semantics), "Preview binds the authenticated actor and observed host and returns verifiable semantics");
                Check(response.Headers.CacheControl?.NoStore == true, "Actor-scoped preview is not cacheable");
                planId = plan!.PlanId; planDigest = plan.PlanDigest;
            }
            var callsBeforeHistory = service.Calls;
            using (var response = await http.GetAsync(TaskPlanningRoutes.Plan(planId)))
            {
                var record = await response.Content.ReadFromJsonAsync<TaskPlanRecord>(RelaxKonOSJsonOptions.Default);
                Check(response.IsSuccessStatusCode && record is { Validity: TaskPlanValidity.Resolved, Revision: 1 }
                    && record.Plan.PlanDigest == planDigest, "The actor can retrieve the same persisted plan by ID");
            }
            http.DefaultRequestHeaders.Authorization = new("Bearer", "other");
            using (var response = await http.GetAsync(TaskPlanningRoutes.Plan(planId)))
                Check(response.StatusCode == HttpStatusCode.NotFound, "Knowing the plan ID cannot reveal another actor's frozen plan");
            using (var response = await http.PostAsJsonAsync(TaskPlanningRoutes.Invalidate(planId), new InvalidateTaskPlanRequest(planDigest, 1), RelaxKonOSJsonOptions.Default))
                Check(response.StatusCode == HttpStatusCode.NotFound, "An actor cannot invalidate another actor's plan");
            http.DefaultRequestHeaders.Authorization = new("Bearer", "read");
            mode.Enabled = false;
            using (var response = await http.GetAsync(TaskPlanningRoutes.Plan(planId)))
                Check(response.IsSuccessStatusCode, "Stored plan history remains readable when host mutation features are unavailable");
            mode.Enabled = true;
            using (var response = await http.PostAsJsonAsync(TaskPlanningRoutes.Invalidate(planId), new InvalidateTaskPlanRequest(new string('0', 64), 1), RelaxKonOSJsonOptions.Default))
                Check(response.StatusCode == HttpStatusCode.Conflict, "Invalidation requires the exact frozen digest");
            using (var response = await http.PostAsync(TaskPlanningRoutes.Invalidate(planId), new StringContent("{\"expectedRevision\":1,\"expectedRevision\":2}", Encoding.UTF8, "application/json")))
                Check(response.StatusCode == HttpStatusCode.BadRequest, "Invalidation rejects duplicate contract fields");
            using (var response = await http.PostAsJsonAsync(TaskPlanningRoutes.Invalidate(planId), new InvalidateTaskPlanRequest(planDigest, 1), RelaxKonOSJsonOptions.Default))
            {
                var record = await response.Content.ReadFromJsonAsync<TaskPlanRecord>(RelaxKonOSJsonOptions.Default);
                Check(response.IsSuccessStatusCode && record is { Validity: TaskPlanValidity.Invalidated, Revision: 2 }
                    && record.Plan.PlanDigest == planDigest && service.Calls == callsBeforeHistory,
                    "Invalidation persists separate validity without changing content or touching domain services");
            }
            var changeIntent = RuntimeChangePlanningChecks.Intent(RuntimeChange.Update);
            ByteArrayContent ChangeBody()
            {
                var body = new ByteArrayContent(TaskPlanJson.WriteIntent(changeIntent));
                body.Headers.ContentType = new("application/json"); return body;
            }
            using (var response = await http.PostAsync(TaskPlanningRoutes.Create, ChangeBody()))
                Check(response.StatusCode == HttpStatusCode.Forbidden && changeService.Calls == 0,
                    "An observer cannot plan privileged component changes");
            http.DefaultRequestHeaders.Authorization = new("Bearer", "wrong-install");
            using (var response = await http.PostAsync(TaskPlanningRoutes.Create, ChangeBody()))
                Check(response.StatusCode == HttpStatusCode.Forbidden && changeService.Calls == 0,
                    "Controller role cannot bypass a different component's installation restriction");
            http.DefaultRequestHeaders.Authorization = new("Bearer", "controller");
            mode.InstallationEnabled = false;
            using (var response = await http.PostAsync(TaskPlanningRoutes.Create, ChangeBody()))
                Check(response.StatusCode == HttpStatusCode.Forbidden && changeService.Calls == 0,
                    "Lifecycle plan creation also requires the host installation feature");
            mode.InstallationEnabled = true;
            using (var response = await http.PostAsync(TaskPlanningRoutes.Create, ChangeBody()))
            {
                var proposed = await response.Content.ReadFromJsonAsync<TaskPlan>(RelaxKonOSJsonOptions.Default);
                Check(response.IsSuccessStatusCode && changeService.Calls == 1 && proposed is not null
                    && proposed.Semantics.Steps.Single() is { Disposition: TaskStepDisposition.Ready, RiskClass: "host-management" },
                    "An authorized controller can freeze and persist a component-change proposal without execution");
                using var storedResponse = await http.GetAsync(TaskPlanningRoutes.Plan(proposed!.PlanId));
                var storedChange = await storedResponse.Content.ReadFromJsonAsync<TaskPlanRecord>(RelaxKonOSJsonOptions.Default);
                Check(storedResponse.IsSuccessStatusCode && storedChange?.Plan.PlanDigest == proposed.PlanDigest,
                    "The exact proposed component change is retrievable by its immutable plan identity");
            }
            using (var response = await http.PostAsync(TaskPlanningRoutes.Preview, ChangeBody()))
                Check(response.StatusCode == HttpStatusCode.BadRequest,
                    "A lifecycle action cannot be smuggled through the read-only inspection route");
            http.DefaultRequestHeaders.Authorization = new("Bearer", "read");
            service.Fail = true;
            using (var response = await http.PostAsync(TaskPlanningRoutes.Preview, Body()))
            {
                var text = await response.Content.ReadAsStringAsync();
                Check(response.StatusCode == HttpStatusCode.ServiceUnavailable && !text.Contains("sensitive", StringComparison.Ordinal)
                    && text.Contains("task.observation_unavailable", StringComparison.Ordinal), "Domain failures return a stable problem without raw diagnostics");
            }
            using (var response = await http.PostAsync(TaskPlanningRoutes.Plan(planId) + "/run", Body()))
                Check(response.StatusCode == HttpStatusCode.NotFound, "Read-only preview exposes no execution route");
            using (var response = await http.PostAsync(TaskPlanningRoutes.Plan(planId) + "/approve", Body()))
                Check(response.StatusCode == HttpStatusCode.NotFound, "Internal approval persistence is not exposed as an unaudited authentication route");
        }
        finally { await app.StopAsync(); Directory.Delete(planRoot, recursive: true); }
        return count;
    }

    private sealed class PreviewService : IRuntimeTaskPreviewService
    {
        public int Calls;
        public bool Fail;
        public Task<TaskPlan> PreviewAsync(string subject, TaskPlanIntent intent, CancellationToken ct)
        {
            Calls++;
            if (Fail) throw new RuntimeLifecycleException("sensitive host diagnostic");
            return new TaskPlanner(RuntimeReadPlanningActions.Create(new Observations())).PlanAsync(new("host", subject), intent, ct);
        }
    }
    private sealed class ChangeService : IRuntimeTaskChangePlanningService
    {
        public int Calls;
        public Task<TaskPlan> PlanAsync(string subject, TaskPlanIntent intent, CancellationToken ct)
        {
            Calls++;
            return new TaskPlanner(RuntimeChangePlanningActions.Create(new RuntimeChangeFixture())).PlanAsync(new("host", subject), intent, ct);
        }
    }
    private sealed class ChangeTransport : IPrivilegedOperationTransport
    {
        public List<PrivilegedOperationKind> Commands { get; } = [];
        private readonly RuntimeChangeFixture _domain = new();
        public bool DriftOnPlan;
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            Commands.Add(request.Operation);
            object result = request.Operation switch
            {
                PrivilegedOperationKind.SingBoxRuntimeQuery => _domain.Installation,
                PrivilegedOperationKind.SingBoxRuntimePlan => await _domain.PlanAsync(request.RuntimeLifecycle!.SourceId, request.RuntimeLifecycle.Plan!, ct),
                _ => throw new Exception("Change planning invoked an execution or mutation Helper verb")
            };
            if (request.Operation == PrivilegedOperationKind.SingBoxRuntimePlan && DriftOnPlan)
                _domain.Installation = _domain.Installation with { ConfigurationIdentity = Guid.NewGuid().ToString("N") };
            return new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(result)));
        }
    }
    private sealed class ReadTransport : IPrivilegedOperationTransport
    {
        public List<PrivilegedOperationKind> Commands { get; } = [];
        private readonly Observations _facts = new();
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            Commands.Add(request.Operation);
            object result = request.Operation switch
            {
                PrivilegedOperationKind.SingBoxRuntimeQuery => _facts.Installation,
                PrivilegedOperationKind.RuntimeCatalogGet => _facts.Catalog,
                _ => throw new Exception("Preview invoked a non-read Helper operation")
            };
            return Task.FromResult(new PrivilegedOperationResult(true,
                OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(result))));
        }
    }
    private sealed class HostModeFixture : IServerModeResolver
    {
        public bool Enabled = true;
        public bool InstallationEnabled = true;
        public ServerMode Mode => ServerMode.System;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
        public bool Supports(ServerHostFeature feature) => Enabled && (feature != ServerHostFeature.AgentInstallation || InstallationEnabled);
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Subject = "00000000-0000-0000-0000-000000000007";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var token = Request.Headers.Authorization.ToString();
            if (!token.StartsWith("Bearer ", StringComparison.Ordinal)) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new("role", token == "Bearer denied" ? "none"
                : token is "Bearer controller" or "Bearer wrong-install" ? "controller" : "observer") };
            if (token == "Bearer wrong-install") claims.Add(new("installation:install", "mihomo"));
            if (token != "Bearer no-subject") claims.Add(new(ClaimTypes.NameIdentifier,
                token == "Bearer other" ? "00000000-0000-0000-0000-000000000008" : Subject));
            return Task.FromResult(AuthenticateResult.Success(new(new(new ClaimsIdentity(claims, "fixture")), "fixture")));
        }
    }
    private sealed class Chunked : HttpContent
    {
        private readonly byte[] _data;
        public Chunked(byte[] data) { _data = data; Headers.ContentType = new("application/json"); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_data).AsTask();
    }
}
