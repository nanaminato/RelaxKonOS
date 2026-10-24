using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Protocol.Workspace;

internal static class TaskPreviewClientChecks
{
    internal static TaskPlan Plan(TaskPlanIntent intent, Guid actor)
    {
        var steps = intent.Steps.Select(step => new TaskStep(step.StepId, step.ActionId, step.SchemaVersion,
            step.ImplementationRevision, step.Inputs.Clone(), step.DependsOn, [], [], [], TaskStepDisposition.NoOp,
            [], "read-only", "none", "none")).ToImmutableArray();
        var semantics = new TaskPlanSemantics("host", actor.ToString("D"), intent.SourceKind, intent.SourceIdentity,
            intent.InputRevision, steps, "stop-on-failure", "safe-boundary", false, TaskPlanLimits.Default);
        return new(Guid.NewGuid(), DateTimeOffset.UtcNow, TaskPlanCanonicalizer.Digest(semantics), semantics);
    }
    public static async Task RunAsync()
    {
        var session = DispatchProxy.Create<IAuthSession, PreviewSession>();
        var auth = (PreviewSession)(object)session;
        var intent = new TaskPlanIntent("app", "relaxkonos.component-center", 1,
            [new("installation", "runtime.verifyInstallation", 1, "sing-box-read-1",
                JsonSerializer.SerializeToElement(new VerifyRuntimeInstallationInput(Guid.NewGuid(), 4)), [])]);
        var plan = Plan(intent, auth.User.Id);
        var calls = 0;
        TaskPlan returned = plan;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            Check(request.RequestUri!.AbsolutePath == TaskPlanningRoutes.Preview && request.Method == HttpMethod.Post,
                "Task preview uses only its closed read-only route");
            var parsed = TaskPlanJson.ReadIntent(request.Content!.ReadAsByteArrayAsync().Result);
            Check(parsed.SourceIdentity == intent.SourceIdentity && request.Headers.Authorization?.Parameter == "token",
                "Preview sends the strict current wire intent with authentication");
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(returned, options: RelaxKonOSJsonOptions.Default) };
        }));
        var client = new TaskPlanPreviewClient(http, session);
        Check((await client.PreviewAsync(intent, "host", default)).PlanDigest == plan.PlanDigest,
            "A matching host, actor and semantic digest is accepted");
        async Task Reject(TaskPlan invalid, string expectedHost = "host")
        {
            returned = invalid;
            try { await client.PreviewAsync(intent, expectedHost, default); throw new Exception("Accepted invalid preview"); }
            catch (InvalidDataException ex) { Check(ex.Message == "task.response_invalid", "Invalid preview fails closed"); }
        }
        await Reject(plan, "another-host");
        await Reject(plan with { PlanDigest = new string('0', 64) });
        var otherActor = plan.Semantics with { CanonicalSubject = Guid.NewGuid().ToString("D") };
        await Reject(plan with { Semantics = otherActor, PlanDigest = TaskPlanCanonicalizer.Digest(otherActor) });
        var changedInput = plan.Semantics with { Steps = [plan.Semantics.Steps[0] with
            { Inputs = JsonSerializer.SerializeToElement(new VerifyRuntimeInstallationInput(Guid.NewGuid(), 4)) }] };
        await Reject(plan with { Semantics = changedInput, PlanDigest = TaskPlanCanonicalizer.Digest(changedInput) });
        var mutation = plan.Semantics with { Steps = [plan.Semantics.Steps[0] with { Disposition = TaskStepDisposition.Ready }] };
        await Reject(plan with { Semantics = mutation, PlanDigest = TaskPlanCanonicalizer.Digest(mutation) });
        var before = calls;
        auth.Login = auth.Login with { Id = Guid.NewGuid() };
        try { await client.PreviewAsync(intent, "host", default); throw new Exception("Used stale session"); }
        catch (InvalidOperationException ex) { Check(ex.Message == "task.session_changed" && calls == before,
            "Changed authentication session cannot send a preview request"); }
        using var switchedHttp = new HttpClient(new Handler(_ =>
        {
            auth.Login = auth.Login with { Id = Guid.NewGuid() };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(plan, options: RelaxKonOSJsonOptions.Default) };
        }));
        var switchedClient = new TaskPlanPreviewClient(switchedHttp, session);
        try { await switchedClient.PreviewAsync(intent, "host", default); throw new Exception("Accepted old reply"); }
        catch (InvalidOperationException ex) { Check(ex.Message == "task.session_changed", "A session change during transport rejects the old reply"); }
        foreach (var body in new[] { "{\"planId\":\"x\",\"planId\":\"y\"}", "null", new string('x', 270000) })
        {
            using var malformedHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
                { Content = new StringContent(body) }));
            try { await new TaskPlanPreviewClient(malformedHttp, session).PreviewAsync(intent, "host", default); throw new Exception("Accepted malformed response"); }
            catch (InvalidDataException) { Check(true, "Malformed, duplicate or oversized preview response is rejected"); }
        }
        using (var deniedHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.Forbidden)
            { Content = new StringContent("sensitive host diagnostic") })))
        {
            try { await new TaskPlanPreviewClient(deniedHttp, session).PreviewAsync(intent, "host", default); throw new Exception("Accepted denial"); }
            catch (HttpRequestException ex) { Check(ex.Message == "task.permission_denied", "Permission errors expose no raw host diagnostic"); }
        }
        foreach (var code in new[] { "task.plan_capacity_exceeded", "task.plan_store_unavailable" })
        {
            using var storageHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable)
                { Content = JsonContent.Create(new { code, detail = "sensitive storage diagnostic" }) }));
            try { await new TaskPlanPreviewClient(storageHttp, session).PreviewAsync(intent, "host", default); throw new Exception("Accepted storage failure"); }
            catch (HttpRequestException ex) { Check(ex.Message == code, "Only allowlisted plan storage problem codes reach the desktop"); }
        }

        var runtimeClient = DispatchProxy.Create<IRuntimeCatalogClient, ReattachmentRuntime>();
        var fixture = (ReattachmentRuntime)(object)runtimeClient;
        fixture.Operation = fixture.Operation with { State = RelaxKonOS.Protocol.Installations.InstallationOperationState.Succeeded };
        using var runtime = new ProxyServerRuntimeViewModel(runtimeClient) { CanRead = true };
        await runtime.LoadAsync();
        var preview = new PreviewFixture(auth.User.Id);
        using var center = new ComponentCenterViewModel(runtime, new(), "server", auth.User.Id, preview);
        await center.InspectCommand.ExecuteAsync(null);
        Check(center.InspectionSteps.Count == 1 && preview.Calls == 1 && !runtime.CanManage && center.Drafts.Count == 0,
            "Observer inspection displays results without management permission, drafts or installation");
        foreach (var failure in new[] { ("task.plan_capacity_exceeded", "component_center.inspection_capacity"),
            ("task.plan_store_unavailable", "component_center.inspection_storage_failed") })
        {
            preview.Error = failure.Item1;
            await center.InspectCommand.ExecuteAsync(null);
            Check(center.InspectionSteps.Count == 0 && center.InspectionNotice.Resolve() == LocalizedText.Get(failure.Item2),
                "Storage failures clear old results and show the specific localized recovery guidance");
        }
        preview.Error = null;
        preview.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = center.InspectCommand.ExecuteAsync(null);
        await runtime.LoadAsync();
        preview.Pending.SetResult(Plan(preview.Last!, auth.User.Id));
        await pending;
        Check(center.InspectionSteps.Count == 0, "A lifecycle refresh discards a preview started against older observations");
        var prior = preview.Calls;
        runtime.CanRead = false;
        await center.InspectCommand.ExecuteAsync(null);
        Check(preview.Calls == prior && !center.CanInspect, "Missing read permission cannot initiate inspection");
    }
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); Console.WriteLine("PASS: " + label); }

    private sealed class PreviewFixture(Guid actor) : ITaskPlanPreviewClient
    {
        public int Calls;
        public TaskPlanIntent? Last;
        public TaskCompletionSource<TaskPlan>? Pending;
        public string? Error;
        public Task<TaskPlan> PreviewAsync(TaskPlanIntent intent, string expectedHost, CancellationToken ct)
        { Calls++; Last = intent; return Error is { } code ? Task.FromException<TaskPlan>(new HttpRequestException(code))
                : Pending?.Task ?? Task.FromResult(Plan(intent, actor)); }
    }
}

public class PreviewSession : DispatchProxy
{
    public UserDto User = new(Guid.NewGuid(), "fixture", HostPlatformKind.Windows, "fixture", DateTimeOffset.UtcNow, null);
    public SessionDto Login = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow, SessionStatus.Active, Guid.NewGuid(), "password", DateTimeOffset.UtcNow);
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_State" => AuthSessionState.Authenticated, "get_CurrentUser" => User, "get_CurrentSession" => Login,
        "get_EffectiveBaseUrl" => "http://localhost/", "GetAccessTokenAsync" => Task.FromResult<string?>("token"),
        _ => throw new NotSupportedException(method?.Name)
    };
}
