using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public interface ITaskExecutionClient
{
    Task<TaskPlanApproval> ApproveAsync(TaskPlan plan, ApproveTaskPlanRequest request, string key, CancellationToken ct);
    Task<TaskPlanApproval> ReadApprovalAsync(TaskPlan plan, Guid approvalId, CancellationToken ct);
    Task<TaskRun> StartAsync(TaskPlan plan, TaskPlanApproval approval, string key, CancellationToken ct);
    Task<TaskRun> ReadAsync(TaskPlan plan, Guid runId, CancellationToken ct);
    Task<TaskRun> RefreshAsync(TaskPlan plan, TaskRun run, CancellationToken ct);
    Task<TaskRun> AdvanceAsync(TaskPlan plan, TaskRun run, CancellationToken ct);
    Task<TaskRun> CancelAsync(TaskPlan plan, TaskRun run, CancellationToken ct);
}

/// <summary>One login and server only. Credentials are never logged or retained by this client.</summary>
public sealed class TaskExecutionClient(HttpClient http, IAuthSession session) : ITaskExecutionClient
{
    private readonly object? _login = session.CurrentSession;
    private readonly Guid? _actor = session.CurrentUser?.Id;
    private readonly Guid? _sessionId = session.CurrentSession?.Id;
    private readonly string? _server = session.ServiceId;
    private readonly string? _baseUrl = session.EffectiveBaseUrl;
    private static readonly JsonSerializerOptions Json = new(RelaxKonOSJsonOptions.Default)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async Task<TaskPlanApproval> ApproveAsync(TaskPlan plan, ApproveTaskPlanRequest request, string key, CancellationToken ct)
    {
        ValidatePlan(plan); ValidateKey(key);
        if (request.PlanDigest != plan.PlanDigest || !request.Confirmed) throw new InvalidOperationException("task.confirmation_required");
        var approval = await SendAsync<TaskPlanApproval>(HttpMethod.Post, TaskPlanningRoutes.Approve(plan.PlanId), request, key, 16384, ct);
        ValidateResponse(() => ValidateApproval(plan, approval));
        if (approval.SessionIdentity != _sessionId?.ToString("D")) Invalid();
        return approval;
    }
    public async Task<TaskRun> StartAsync(TaskPlan plan, TaskPlanApproval approval, string key, CancellationToken ct)
    {
        ValidatePlan(plan); ValidateResponse(() => ValidateApproval(plan, approval)); ValidateKey(key);
        if (approval.SessionIdentity != _sessionId?.ToString("D")) Invalid();
        if (approval.State is not (TaskApprovalState.Granted or TaskApprovalState.Consumed)) throw new InvalidOperationException("task.approval_unavailable");
        var run = await SendAsync<TaskRun>(HttpMethod.Post, TaskPlanningRoutes.StartRun(plan.PlanId), new StartTaskRunRequest(approval.ApprovalId, plan.PlanDigest), key, 65536, ct);
        ValidateResponse(() => ValidateRun(plan, run));
        if (run.ApprovalId != approval.ApprovalId || run.SessionIdentity != approval.SessionIdentity || run.PolicyRevision != approval.PolicyRevision
            || approval.RunId is { } id && run.RunId != id) Invalid();
        return run;
    }
    public async Task<TaskPlanApproval> ReadApprovalAsync(TaskPlan plan, Guid approvalId, CancellationToken ct)
    {
        ValidatePlan(plan); if (approvalId == Guid.Empty) Invalid();
        var approval = await SendAsync<TaskPlanApproval>(HttpMethod.Get, TaskPlanningRoutes.Approval(approvalId), null, null, 16384, ct);
        ValidateResponse(() => ValidateApproval(plan, approval));
        if (approval.ApprovalId != approvalId) Invalid();
        return approval;
    }
    public async Task<TaskRun> ReadAsync(TaskPlan plan, Guid runId, CancellationToken ct)
    {
        ValidatePlan(plan); if (runId == Guid.Empty) Invalid();
        var run = await SendAsync<TaskRun>(HttpMethod.Get, TaskPlanningRoutes.Run(runId), null, null, 65536, ct);
        ValidateResponse(() => ValidateRun(plan, run)); if (run.RunId != runId) Invalid(); return run;
    }
    public Task<TaskRun> AdvanceAsync(TaskPlan plan, TaskRun run, CancellationToken ct) => ChangeRunAsync(plan, run, false, ct);
    public Task<TaskRun> CancelAsync(TaskPlan plan, TaskRun run, CancellationToken ct) => ChangeRunAsync(plan, run, true, ct);
    public async Task<TaskRun> RefreshAsync(TaskPlan plan, TaskRun previous, CancellationToken ct)
    {
        ValidatePlan(plan); ValidateResponse(() => ValidateRun(plan, previous));
        var run = await ReadAsync(plan, previous.RunId, ct); ValidateContinuity(previous, run); return run;
    }
    private async Task<TaskRun> ChangeRunAsync(TaskPlan plan, TaskRun previous, bool cancel, CancellationToken ct)
    {
        ValidatePlan(plan); ValidateResponse(() => ValidateRun(plan, previous));
        var run = await SendAsync<TaskRun>(HttpMethod.Post, cancel ? TaskPlanningRoutes.CancelRun(previous.RunId) : TaskPlanningRoutes.Advance(previous.RunId),
            new TaskRunRevisionRequest(previous.Revision), null, 65536, ct);
        ValidateResponse(() => ValidateRun(plan, run));
        ValidateContinuity(previous, run);
        return run;
    }
    private static void ValidateContinuity(TaskRun previous, TaskRun run)
    {
        if (run.RunId != previous.RunId || run.ApprovalId != previous.ApprovalId || run.SessionIdentity != previous.SessionIdentity
            || run.PolicyRevision != previous.PolicyRevision || run.CreatedAt != previous.CreatedAt || run.Revision < previous.Revision
            || previous.CancellationRequested && !run.CancellationRequested
            || Terminal(previous.State) && run.State != previous.State
            || run.Revision == previous.Revision && !TaskPlanCanonicalizer.Canonicalize(TaskPlanJson.FreezeInputs(run))
                .SequenceEqual(TaskPlanCanonicalizer.Canonicalize(TaskPlanJson.FreezeInputs(previous)))
            || run.Steps.Where((step, index) => step.IdempotencyKey != previous.Steps[index].IdempotencyKey
                || previous.Steps[index].RequestDigest is { } digest && digest != step.RequestDigest
                || previous.Steps[index].DomainOperationId is { } operation && operation != step.DomainOperationId
                || TerminalStep(previous.Steps[index].State) && (previous.Steps[index].State != step.State || previous.Steps[index].ProblemCode != step.ProblemCode)).Any()) Invalid();
    }
    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, string? key, int maximum, CancellationToken ct)
    {
        Verify();
        var token = await session.GetAccessTokenAsync(TimeSpan.FromMinutes(1), ct: ct); Verify();
        using var request = new HttpRequestMessage(method, new Uri(new Uri(_baseUrl!), route.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); Verify();
        var limit = response.IsSuccessStatusCode ? maximum : 16384;
        if (response.Content.Headers.ContentLength > limit) Invalid();
        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream(); var bytes = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(bytes, ct); if (read == 0) break;
            if (buffer.Length + read > limit) Invalid(); buffer.Write(bytes, 0, read);
        }
        Verify();
        if (!response.IsSuccessStatusCode)
        {
            var problem = response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden ? "task.permission_denied" : "task.request_failed";
            try
            {
                using var error = JsonDocument.Parse(buffer.ToArray());
                if (error.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                    && code.GetString() is { } value && SafeProblem(value)) problem = value;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
            throw new HttpRequestException(problem, null, response.StatusCode);
        }
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            TaskPlanCanonicalizer.Canonicalize(document.RootElement);
            return document.RootElement.Deserialize<T>(Json) ?? throw new JsonException();
        }
        catch (Exception ex) when (ex is JsonException or TaskPlanningException or ArgumentException or InvalidOperationException)
        { throw new InvalidDataException("task.response_invalid", ex); }
    }
    private void Verify()
    {
        if (_login is null || _actor is null || !ReferenceEquals(_login, session.CurrentSession) || session.State != AuthSessionState.Authenticated
            || session.CurrentUser?.Id != _actor || session.ServiceId != _server || session.EffectiveBaseUrl != _baseUrl
            || string.IsNullOrWhiteSpace(_server) || !Uri.TryCreate(_baseUrl, UriKind.Absolute, out var address)
            || address.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(address.UserInfo))
            throw new InvalidOperationException("task.session_changed");
    }
    private void ValidatePlan(TaskPlan plan)
    {
        Verify();
        try
        {
            if (plan.PlanId == Guid.Empty || plan.Semantics.CanonicalSubject != _actor?.ToString("D") || !Text(plan.Semantics.HostId)
                || plan.Semantics.Steps.IsDefaultOrEmpty || TaskPlanCanonicalizer.Digest(plan.Semantics) != plan.PlanDigest) Invalid();
        }
        catch (Exception ex) when (ex is TaskPlanningException or NullReferenceException or ArgumentException)
        { throw new InvalidDataException("task.response_invalid", ex); }
    }
    private static void ValidateApproval(TaskPlan plan, TaskPlanApproval approval)
    {
        var capabilities = plan.Semantics.Steps.SelectMany(step => step.RequiredCapabilities).Distinct().OrderBy(item => item.Capability).ThenBy(item => item.Target).ToArray();
        if (approval is null || approval.ApprovalId == Guid.Empty || approval.PlanId != plan.PlanId || approval.PlanDigest != plan.PlanDigest
            || approval.CanonicalSubject != plan.Semantics.CanonicalSubject || approval.HostId != plan.Semantics.HostId || !Text(approval.SessionIdentity)
            || !Text(approval.PolicyRevision) || !Text(approval.ApproverIdentity) || !Text(approval.AuthenticationMethod)
            || !Enum.IsDefined(approval.State) || approval.Revision is < 1 or > 3 || approval.RunId == Guid.Empty
            || approval.CreatedAt < plan.CreatedAt
            || (approval.State == TaskApprovalState.Consumed && approval.RunId is null)
            || (approval.State == TaskApprovalState.Revoked) != (approval.RevokedAt is not null)
            || approval.StartBefore <= approval.CreatedAt || approval.StartBefore > approval.CreatedAt.AddMinutes(2)
            || approval.RunAuthorizationExpiresAt < approval.StartBefore || approval.RunAuthorizationExpiresAt > approval.CreatedAt.AddMinutes(10)
            || approval.Capabilities.IsDefault || !approval.Capabilities.OrderBy(item => item.Capability).ThenBy(item => item.Target).SequenceEqual(capabilities)
            || capabilities is not [{ Capability: "SingBoxInstall", Target: "singbox" }]) Invalid();
    }
    private static void ValidateRun(TaskPlan plan, TaskRun run)
    {
        if (run is null || run.RunId == Guid.Empty || run.ApprovalId == Guid.Empty || run.PlanId != plan.PlanId || run.PlanDigest != plan.PlanDigest
            || run.CanonicalSubject != plan.Semantics.CanonicalSubject || run.HostId != plan.Semantics.HostId || !Text(run.SessionIdentity) || !Text(run.PolicyRevision)
            || !Enum.IsDefined(run.State) || run.Revision is < 1 or > 194 || run.CreatedAt < plan.CreatedAt || run.Steps.IsDefault
            || run.Steps.Length != plan.Semantics.Steps.Length || !run.Steps.Select(step => step?.StepId).SequenceEqual(plan.Semantics.Steps.Select(step => step.StepId))) Invalid();
        for (var index = 0; index < run.Steps.Length; index++)
        {
            var step = run.Steps[index]; var read = plan.Semantics.Steps[index].RequiredCapabilities.IsEmpty;
            if (step is null || !Enum.IsDefined(step.State) || !Hash(step.IdempotencyKey) || step.DomainOperationId == Guid.Empty
                || step.RequestDigest is { } digest && !Hash(digest)) Invalid();
            if (step.State == TaskStepRunState.NoOp && !read || read && step.State is not (TaskStepRunState.Pending or TaskStepRunState.NoOp or TaskStepRunState.Skipped or TaskStepRunState.Failed)) Invalid();
            if (step.State is TaskStepRunState.Pending or TaskStepRunState.NoOp or TaskStepRunState.Skipped || read)
            { if (step.RequestDigest is not null || step.DomainOperationId is not null) Invalid(); }
            else if (step.RequestDigest is null || step.State == TaskStepRunState.Dispatching && step.DomainOperationId is not null
                || step.State != TaskStepRunState.Dispatching && step.DomainOperationId is null
                    && !(step.State == TaskStepRunState.Cancelled && run.CancellationRequested && step.ProblemCode == "task.cancelled_before_acceptance")) Invalid();
        }
        if (run.State == TaskRunState.Queued && (run.CancellationRequested || run.Steps.Any(step => step.State != TaskStepRunState.Pending))
            || run.State == TaskRunState.Succeeded && run.Steps.Any(step => step.State is not (TaskStepRunState.Succeeded or TaskStepRunState.NoOp))
            || run.State == TaskRunState.Cancelling && (!run.CancellationRequested || !run.Steps.Any(step => step.State is TaskStepRunState.Running or TaskStepRunState.Dispatching))
            || run.State == TaskRunState.Failed && !run.Steps.Any(step => step.State == TaskStepRunState.Failed)
            || run.State == TaskRunState.NeedsRecovery && !run.Steps.Any(step => step.State == TaskStepRunState.NeedsRecovery)
            || Terminal(run.State) && run.Steps.Any(step => step.State is TaskStepRunState.Pending or TaskStepRunState.Dispatching or TaskStepRunState.Running)) Invalid();
    }
    private static bool Terminal(TaskRunState state) => state is TaskRunState.Succeeded or TaskRunState.Failed or TaskRunState.Cancelled or TaskRunState.NeedsRecovery;
    private static bool TerminalStep(TaskStepRunState state) => state is TaskStepRunState.Succeeded or TaskStepRunState.NoOp or TaskStepRunState.Skipped or TaskStepRunState.Failed or TaskStepRunState.Cancelled or TaskStepRunState.NeedsRecovery;
    private static void ValidateResponse(Action validate)
    {
        try { validate(); }
        catch (Exception ex) when (ex is NullReferenceException or ArgumentException or InvalidOperationException or TaskPlanningException)
        { throw new InvalidDataException("task.response_invalid", ex); }
    }
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl);
    private static bool Hash(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    private static void ValidateKey(string key)
    { if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(c => c < 33 || c > 126)) throw new InvalidOperationException("task.idempotency_required"); }
    [DoesNotReturn] private static void Invalid() => throw new InvalidDataException("task.response_invalid");
    private static bool SafeProblem(string code) => code is "task.permission_denied" or "task.session_unavailable" or "task.policy_changed" or "task.plan_changed"
        or "task.plan_expired" or "task.plan_not_found" or "task.approval_not_found" or "task.approval_unavailable" or "task.approval_identity_invalid"
        or "task.administrator_authentication_failed" or "task.confirmation_required" or "task.run_revision_conflict" or "task.run_not_found"
        or "task.domain_unavailable" or "task.observation_unavailable" or "task.plan_store_unavailable" or "task.idempotency_conflict"
        or "task.plan_already_started" or "task.execution_authority_invalid" or "task.execution_authority_expired" or "task.step_not_ready"
        or "task.result_conflict" or "task.domain_operation_conflict";
}
