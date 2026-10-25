using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.TaskOrchestration;

internal static class TaskExecutionHttpChecks
{
    private const string Subject = "11111111-1111-1111-1111-111111111111";
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        var root = Path.Combine(Path.GetTempPath(), "task-execution-http-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var store = new TaskPlanStore(Path.Combine(root, "plans.db"), new EphemeralDataProtectionProvider());
        var domain = new RuntimeChangeFixture(); var planning = new Planning(domain);
        var plan = await planning.PlanAsync(Subject, RuntimeChangePlanningChecks.Intent(RuntimeChange.Update), default); store.Save(plan, Subject);
        var policy = new Policies(); var admin = new Administrator(); var mode = new HostModeFixture();
        var dispatch = new Dispatch(store, domain); var revalidator = new RuntimeTaskPlanRevalidator(planning);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = root });
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, Authentication>("fixture", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(RuntimeCatalogEndpoints.ReadPolicy, p => p.RequireAuthenticatedUser().RequireClaim("role", "controller", "observer"));
            options.AddPolicy(RuntimeCatalogEndpoints.ManagePolicy, p => p.RequireAuthenticatedUser().RequireClaim("role", "controller"));
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Request.Headers["X-Limit"] == "one" ? "one" : "normal",
                key => new FixedWindowRateLimiterOptions { PermitLimit = key == "one" ? 1 : 1000, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        builder.Services.AddSingleton<IServerModeResolver>(mode); builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(new TaskPlanApprovalService(store, policy, revalidator, admin));
        builder.Services.AddSingleton(new TaskRunStartService(store, policy, revalidator));
        builder.Services.AddSingleton(new TaskRunAdvancementService(store, policy, null!, dispatch, dispatch));
        builder.Services.AddSingleton<InstallationOperationStore>();
        builder.Services.AddSingleton<InstallationCoordinator>(); // Empty domain registry: HTTP tests never submit privileged work.
        builder.Services.AddSingleton(services => new TaskRunCancellationService(store, policy, dispatch, services.GetRequiredService<InstallationCoordinator>()));
        await using var app = builder.Build(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.MapTaskExecutionEndpoints();
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new(app.Urls.Single()) };
            ApproveTaskPlanRequest Approval(bool confirmed = true) => new() { PlanDigest = plan.PlanDigest, Confirmed = confirmed, AdministratorUsername = "display-name", Password = "fixture-secret" };
            async Task<HttpResponseMessage> Post(string route, object body, string? key = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default) };
                if (key is not null) request.Headers.Add("Idempotency-Key", key);
                return await http.SendAsync(request);
            }
            using (var response = await Post(TaskPlanningRoutes.Approve(plan.PlanId), Approval(), "approve"))
                Check(response.StatusCode == HttpStatusCode.Unauthorized && admin.Calls == 0, "anonymous approval cannot authenticate an administrator");
            foreach (var token in new[] { "read", "restricted", "dual", "no-subject" })
            {
                http.DefaultRequestHeaders.Authorization = new("Bearer", token);
                using var response = await Post(TaskPlanningRoutes.Approve(plan.PlanId), Approval(), "approve");
                Check(response.StatusCode == HttpStatusCode.Forbidden && admin.Calls == 0, "role/target/ambiguous or missing actor is rejected before authentication");
            }
            http.DefaultRequestHeaders.Authorization = new("Bearer", "manage"); mode.Enabled = false;
            using (var response = await Post(TaskPlanningRoutes.Approve(plan.PlanId), Approval(), "approve"))
                Check(response.StatusCode == HttpStatusCode.Forbidden && admin.Calls == 0, "host deployment features gate task approval");
            mode.Enabled = true;
            Check(!Approval().ToString()!.Contains("fixture-secret", StringComparison.Ordinal), "approval DTO string formatting does not include credentials");
            using (var response = await Post(TaskPlanningRoutes.Approve(plan.PlanId), Approval()))
                Check(response.StatusCode == HttpStatusCode.BadRequest && admin.Calls == 0, "approval requires a bounded idempotency key");
            using (var response = await Post(TaskPlanningRoutes.Approve(plan.PlanId), Approval(false), "unconfirmed"))
                Check(response.StatusCode == HttpStatusCode.BadRequest && admin.Calls == 0, "explicit confirmation precedes administrator authentication");
            using (var request = new HttpRequestMessage(HttpMethod.Post, TaskPlanningRoutes.Approve(plan.PlanId)) { Content = new StringContent("{}") })
            {
                request.Headers.Add("Idempotency-Key", "content-type"); using var response = await http.SendAsync(request);
                Check(response.StatusCode == HttpStatusCode.UnsupportedMediaType && admin.Calls == 0, "approval rejects non-JSON content before authentication");
            }
            using (var request = new HttpRequestMessage(HttpMethod.Post, TaskPlanningRoutes.Approve(plan.PlanId)) { Content = new Chunked(new byte[16385]) })
            {
                request.Headers.Add("Idempotency-Key", "chunked"); using var response = await http.SendAsync(request);
                Check(response.StatusCode == HttpStatusCode.RequestEntityTooLarge && admin.Calls == 0, "chunked approval input is bounded without a declared length");
            }
            foreach (var body in new[] { "{\"planDigest\":\"a\",\"planDigest\":\"b\"}", "{\"role\":\"controller\"}", "null", new string('x', 16385) })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, TaskPlanningRoutes.Approve(plan.PlanId)) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                request.Headers.Add("Idempotency-Key", "invalid"); using var response = await http.SendAsync(request);
                Check((response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.RequestEntityTooLarge) && admin.Calls == 0,
                    "duplicate/unknown/null/oversized approval input cannot reach administrator authentication");
            }
            admin.Denied = true;
            using (var response = await Post(TaskPlanningRoutes.Approve(plan.PlanId), Approval(), "bad-password"))
                Check(response.StatusCode == HttpStatusCode.Forbidden && !(await response.Content.ReadAsStringAsync()).Contains("fixture-secret"), "administrator denial exposes only sanitized task errors");
            admin.Denied = false;
            TaskPlanApproval approval;
            using (var response = await Post(TaskPlanningRoutes.Approve(plan.PlanId), Approval(), "approve"))
            {
                approval = (await response.Content.ReadFromJsonAsync<TaskPlanApproval>(RelaxKonOSJsonOptions.Default))!;
                Check(response.StatusCode == HttpStatusCode.OK && approval.ApproverIdentity == "linux:0" && response.Headers.CacheControl?.NoStore == true,
                    "approval returns exact metadata without credentials and disables caching");
            }
            http.DefaultRequestHeaders.Authorization = new("Bearer", "other");
            using (var response = await http.GetAsync(TaskPlanningRoutes.Approval(approval.ApprovalId)))
                Check(response.StatusCode == HttpStatusCode.NotFound, "another actor cannot read approval metadata or its run binding");
            using (var response = await Post(TaskPlanningRoutes.StartRun(plan.PlanId), new StartTaskRunRequest(approval.ApprovalId, plan.PlanDigest), "start"))
                Check(response.StatusCode == HttpStatusCode.NotFound, "another actor cannot consume an approval");
            http.DefaultRequestHeaders.Authorization = new("Bearer", "manage");
            using (var response = await Post(TaskPlanningRoutes.StartRun(plan.PlanId), new StartTaskRunRequest(approval.ApprovalId, plan.PlanDigest)))
                Check(response.StatusCode == HttpStatusCode.BadRequest && store.GetApproval(approval.ApprovalId, Subject)?.State == TaskApprovalState.Granted,
                    "missing start idempotency key cannot consume an approval");
            TaskRun run;
            using (var response = await Post(TaskPlanningRoutes.StartRun(plan.PlanId), new StartTaskRunRequest(approval.ApprovalId, plan.PlanDigest), "start"))
            {
                run = (await response.Content.ReadFromJsonAsync<TaskRun>(RelaxKonOSJsonOptions.Default))!;
                Check(response.StatusCode == HttpStatusCode.Accepted && run.State == TaskRunState.Queued && dispatch.Starts == 0
                    && response.Headers.Location?.ToString() == TaskPlanningRoutes.Run(run.RunId), "start only consumes approval and returns durable queued identity");
            }
            using (var response = await Post(TaskPlanningRoutes.StartRun(plan.PlanId), new StartTaskRunRequest(approval.ApprovalId, plan.PlanDigest), "start"))
                Check((await response.Content.ReadFromJsonAsync<TaskRun>(RelaxKonOSJsonOptions.Default))?.RunId == run.RunId && dispatch.Starts == 0,
                    "start retry cannot allocate or dispatch another run");
            using (var response = await Post(TaskPlanningRoutes.StartRun(plan.PlanId), new StartTaskRunRequest(approval.ApprovalId, new string('0', 64)), "start"))
                Check(response.StatusCode == HttpStatusCode.Conflict && dispatch.Starts == 0, "start key cannot be reused for another digest");
            http.DefaultRequestHeaders.Authorization = new("Bearer", "dual");
            using (var response = await http.GetAsync(TaskPlanningRoutes.Run(run.RunId))) Check(response.StatusCode == HttpStatusCode.Forbidden, "ambiguous actor claims cannot read run history");
            http.DefaultRequestHeaders.Authorization = new("Bearer", "other");
            using (var response = await http.GetAsync(TaskPlanningRoutes.Run(run.RunId))) Check(response.StatusCode == HttpStatusCode.NotFound, "run history is isolated by actor");
            http.DefaultRequestHeaders.Authorization = new("Bearer", "read");
            var authenticationCalls = admin.Calls;
            using (var response = await http.GetAsync(TaskPlanningRoutes.Approval(approval.ApprovalId)))
                Check((await response.Content.ReadFromJsonAsync<TaskPlanApproval>(RelaxKonOSJsonOptions.Default)) is { State: TaskApprovalState.Consumed } accepted
                    && accepted.RunId == run.RunId && admin.Calls == authenticationCalls && response.Headers.CacheControl?.NoStore == true,
                    "owner read-only approval lookup recovers accepted run identity without administrator authentication or consumption");
            using (var response = await http.GetAsync(TaskPlanningRoutes.Run(run.RunId))) Check(response.StatusCode == HttpStatusCode.OK, "owner with read permission can inspect run history");
            using (var response = await Post(TaskPlanningRoutes.Advance(run.RunId), new TaskRunRevisionRequest(1)))
                Check(response.StatusCode == HttpStatusCode.Forbidden && dispatch.Starts == 0, "read-only history access cannot advance execution");
            http.DefaultRequestHeaders.Authorization = new("Bearer", "manage");
            using (var response = await Post(TaskPlanningRoutes.Advance(run.RunId), new { expectedRevision = 1, role = "controller" }))
                Check(response.StatusCode == HttpStatusCode.BadRequest && dispatch.Starts == 0, "unknown advance parameters cannot inject execution authority");
            using (var response = await Post(TaskPlanningRoutes.Advance(run.RunId), new TaskRunRevisionRequest(99)))
                Check(response.StatusCode == HttpStatusCode.Conflict && dispatch.Starts == 0, "stale revision cannot schedule a step");
            using (var response = await Post(TaskPlanningRoutes.Advance(run.RunId), new TaskRunRevisionRequest(1)))
            { run = (await response.Content.ReadFromJsonAsync<TaskRun>(RelaxKonOSJsonOptions.Default))!; Check(run.Revision == 3 && dispatch.Starts == 1, "explicit advance submits one exact step through the dispatcher boundary"); }
            using (var response = await http.GetAsync(TaskPlanningRoutes.Run(run.RunId))) Check(response.StatusCode == HttpStatusCode.OK && dispatch.Starts == 1, "GET never schedules work");
            using (var response = await Post(TaskPlanningRoutes.CancelRun(run.RunId), new TaskRunRevisionRequest(3)))
            { run = (await response.Content.ReadFromJsonAsync<TaskRun>(RelaxKonOSJsonOptions.Default))!; Check(run.State == TaskRunState.Cancelling && run.Revision == 4, "cancel acknowledges only durable intent for an uncancellable active operation"); }
            using (var response = await Post(TaskPlanningRoutes.Advance(run.RunId), new TaskRunRevisionRequest(4)))
            { run = (await response.Content.ReadFromJsonAsync<TaskRun>(RelaxKonOSJsonOptions.Default))!; Check(run.State == TaskRunState.Succeeded && dispatch.Starts == 1, "receipt reconciliation preserves committed success after cancellation"); }
            mode.Enabled = false;
            using (var response = await http.GetAsync(TaskPlanningRoutes.Run(run.RunId))) Check(response.StatusCode == HttpStatusCode.OK, "host feature loss does not hide durable history");
            using (var response = await Post(TaskPlanningRoutes.Advance(run.RunId), new TaskRunRevisionRequest(run.Revision))) Check(response.StatusCode == HttpStatusCode.Forbidden, "feature loss still blocks mutation routes");
            mode.Enabled = true;
            var driftPlan = plan with { PlanId = Guid.NewGuid() }; store.Save(driftPlan, Subject);
            TaskPlanApproval driftApproval;
            using (var response = await Post(TaskPlanningRoutes.Approve(driftPlan.PlanId), Approval(), "drift-approve"))
                driftApproval = (await response.Content.ReadFromJsonAsync<TaskPlanApproval>(RelaxKonOSJsonOptions.Default))!;
            var originalRelease = domain.Release; domain.Release = originalRelease with { Artifact = originalRelease.Artifact with { Sha256 = new string('c', 64) } };
            using (var response = await Post(TaskPlanningRoutes.StartRun(driftPlan.PlanId), new StartTaskRunRequest(driftApproval.ApprovalId, driftPlan.PlanDigest), "drift-start"))
                Check(response.StatusCode == HttpStatusCode.Conflict && store.GetApproval(driftApproval.ApprovalId, Subject) is { State: TaskApprovalState.Granted, RunId: null },
                    "artifact drift after approval cannot consume the grant or create a run");
            domain.Release = originalRelease;
            http.DefaultRequestHeaders.Remove("X-Limit"); http.DefaultRequestHeaders.Add("X-Limit", "one");
            using (var response = await Post(TaskPlanningRoutes.Approve(plan.PlanId), Approval(), "rate-one")) { }
            var calls = admin.Calls;
            using (var response = await Post(TaskPlanningRoutes.Approve(plan.PlanId), Approval(), "rate-two"))
                Check(response.StatusCode == HttpStatusCode.TooManyRequests && admin.Calls == calls, "credential approval uses the shared login rate limiter before authentication");
        }
        finally { await app.StopAsync(); Directory.Delete(root, true); }
        return count;
    }
    private sealed class Planning(RuntimeChangeFixture domain) : IRuntimeTaskChangePlanningService
    { public Task<TaskPlan> PlanAsync(string subject, TaskPlanIntent intent, CancellationToken ct) => new TaskPlanner(RuntimeChangePlanningActions.Create(domain)).PlanAsync(new("host", subject), intent, ct); }
    private sealed class Policies : ITaskApprovalPolicyReader, ITaskExecutionPolicyReader
    {
        public TaskExecutionPolicyObservation Read(ClaimsPrincipal principal, string boundHostId) => new(new(principal.FindFirst("sub")!.Value, "session", "host", "policy"), DateTimeOffset.UtcNow.AddMinutes(2));
        public Task<TaskApprovalObservation> ReadAsync(ClaimsPrincipal principal, CancellationToken ct) => Task.FromResult(new TaskApprovalObservation(Read(principal, "host").Identity,
            new("1000", "alice", HostPlatformKind.Linux, "Alice", null), HostAccountPrivilege.StandardUser, DateTimeOffset.UtcNow.AddMinutes(2), new RuntimeChangeFixture().Installation));
    }
    private sealed class Administrator : IHostAdministratorAuthenticator
    {
        public int Calls; public bool Denied;
        public HostAdministratorAuthenticationResult Authenticate(string currentUsername, string? administratorUsername, string? password)
        { Calls++; return Denied ? new(false, "fixture-secret", "none", null) : new(true, "", "fixture", "linux:0"); }
    }
    private sealed class HostModeFixture : IServerModeResolver
    {
        public bool Enabled = true;
        public ServerMode Mode => ServerMode.System;
        public bool Supports(ServerHostFeature feature) => Enabled;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    }
    private sealed class Dispatch(TaskPlanStore store, RuntimeChangeFixture fixture) : ITaskStepDispatcher, ITaskRunReconciler, ITaskReconciliationDomain
    {
        public int Starts; private InstallationOperationDto _operation = null!;
        public Task<InstallationOperationDto> DispatchAsync(ClaimsPrincipal principal, Guid runId, string stepId, CancellationToken ct)
        {
            Starts++; var run = store.GetRun(runId, Subject)!; var intent = store.PrepareDispatch(runId, stepId, new(Subject, "session", "host", "policy"), run.Revision);
            var id = Guid.NewGuid(); store.BindDomainOperation(runId, stepId, Subject, intent.RequestDigest, id, run.Revision + 1);
            return Task.FromResult(_operation = new(id, InstallationServiceId.SingBox, InstallationOperationKind.Upgrade, InstallationOperationState.Running,
                InstallationStage.Activating, null, null, run.CreatedAt, run.CreatedAt, null, false));
        }
        public Task<TaskRun> ReconcileAsync(Guid runId, string stepId, string subject, CancellationToken ct)
        {
            var run = store.GetRun(runId, subject)!;
            return Task.FromResult(store.RecordReconciliation(runId, subject, new(stepId, _operation with
                { State = InstallationOperationState.Succeeded, Stage = InstallationStage.Completed, CompletedAt = DateTimeOffset.UtcNow }, fixture.Installation with
                { Installation = fixture.Installation.Installation with { StateRevision = 8, ActiveReleaseId = "target" }, MeasuredVersion = "1.14.3", Receipt = new(_operation.OperationId, RuntimeSwitchOutcome.Succeeded, null) }), run.Revision));
        }
        public InstallationOperationDto? Find(Guid runId, TaskDispatchIntent dispatch, string subject) => _operation;
        public Task<RuntimeInstallationSnapshot> ObserveAsync(Guid operationId, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var token = Request.Headers.Authorization.ToString().Replace("Bearer ", "");
            if (string.IsNullOrEmpty(token)) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new("role", token == "read" ? "observer" : "controller") };
            if (token != "no-subject") claims.Add(new("sub", token == "other" ? "22222222-2222-2222-2222-222222222222" : Subject));
            if (token == "restricted") claims.Add(new("installation:install", "docker"));
            if (token == "dual") claims.Add(new(ClaimTypes.NameIdentifier, "different"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "fixture")), "fixture")));
        }
    }
    private sealed class Chunked : HttpContent
    {
        private readonly byte[] _bytes;
        public Chunked(byte[] bytes) { _bytes = bytes; Headers.ContentType = new("application/json"); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes).AsTask();
    }
}
