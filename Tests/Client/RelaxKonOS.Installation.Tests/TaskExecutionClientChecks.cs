using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;

internal static class TaskExecutionClientChecks
{
    public static async Task RunAsync()
    {
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS: Execution client " + label); }
        var session = DispatchProxy.Create<IAuthSession, PreviewSession>(); var auth = (PreviewSession)(object)session;
        var intent = new TaskPlanIntent("app", "relaxkonos.component-center", 1,
            [new("change", "runtime.uninstall", 1, "sing-box-change-1", TaskPlanJson.FreezeInputs(new RuntimeTaskChangeInput(Guid.NewGuid(), "host", 7, 0, 0, null, null, false, true)), [])]);
        var plan = TaskPreviewClientChecks.ChangePlan(intent, auth.User.Id);
        var now = plan.CreatedAt.AddSeconds(1);
        var approval = new TaskPlanApproval(Guid.NewGuid(), plan.PlanId, plan.PlanDigest, TaskApprovalState.Granted, 1,
            auth.User.Id.ToString("D"), auth.Login.Id.ToString("D"), "host", "linux:0", "fixture", "policy", now, now.AddMinutes(1), now.AddMinutes(5), null,
            [new("SingBoxInstall", "singbox")], null);
        var queued = new TaskRun(Guid.NewGuid(), plan.PlanId, plan.PlanDigest, approval.ApprovalId, auth.User.Id.ToString("D"), auth.Login.Id.ToString("D"), "host", "policy",
            TaskRunState.Queued, 1, false, now.AddSeconds(1), [new("change", TaskStepRunState.Pending, new string('a', 64), null, null, null)], null);
        var path = TaskPlanningRoutes.Approve(plan.PlanId); object returned = approval; var calls = 0;
        var checkedRoutes = new HashSet<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            calls++; Check(request.RequestUri!.AbsolutePath == path && request.Headers.Authorization?.Parameter == "token", "uses exact route and current bearer token");
            if (path.EndsWith("/recovery", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                var recoveryBody = System.Text.Json.JsonSerializer.Deserialize<TaskRecoveryRequest>(request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult(), RelaxKonOSJsonOptions.Default)!;
                Check(request.Headers.GetValues("Idempotency-Key").Single() == "recover" && recoveryBody.Confirmed
                    && recoveryBody.Action == TaskRecoveryAction.EmergencyStop && recoveryBody.ExpectedRevision == 4,
                    "recovery POST carries explicit action, confirmation, observed revision and stable key");
            }
            if (checkedRoutes.Add(path))
            {
                if (path == TaskPlanningRoutes.Approve(plan.PlanId))
                {
                    var commandBody = TaskPlanJson.ReadApproval(request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                    Check(request.Method == HttpMethod.Post && request.Headers.GetValues("Idempotency-Key").Single() == "approve"
                        && commandBody.PlanDigest == plan.PlanDigest && commandBody.Confirmed && commandBody.Password == "fixture-secret",
                        "approval sends the strict confirmed plan and stable request key");
                }
                else if (path == TaskPlanningRoutes.StartRun(plan.PlanId))
                {
                    var commandBody = TaskPlanJson.ReadStart(request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                    Check(request.Method == HttpMethod.Post && request.Headers.GetValues("Idempotency-Key").Single() == "start"
                        && commandBody.ApprovalId == approval.ApprovalId && commandBody.PlanDigest == plan.PlanDigest,
                        "start carries only the exact approval and plan digest under its stable key");
                }
                else if (request.Method == HttpMethod.Get)
                    Check(request.Method == HttpMethod.Get && request.Content is null, "read is a bodyless GET and cannot submit a command");
                else
                    Check(request.Method == HttpMethod.Post && TaskPlanJson.ReadRunRevision(request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult()).ExpectedRevision
                        == (path.EndsWith("/recheck-recovery", StringComparison.Ordinal) ? 4 : path.EndsWith("/cancel", StringComparison.Ordinal) ? 3 : 1), "advance/cancel/recovery check sends the caller's exact observed revision");
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(returned, returned.GetType(), options: RelaxKonOSJsonOptions.Default) };
        }));
        var client = new TaskExecutionClient(http, session);
        var command = new ApproveTaskPlanRequest { PlanDigest = plan.PlanDigest, Confirmed = true, AdministratorUsername = "administrator", Password = "fixture-secret" };
        Check((await client.ApproveAsync(plan, command, "approve", default)).ApprovalId == approval.ApprovalId, "accepts only matching approval scope");
        foreach (var invalid in new[] { approval with { HostId = "other" }, approval with { SessionIdentity = Guid.NewGuid().ToString("D") }, approval with { PlanDigest = new string('0', 64) },
            approval with { Capabilities = [new("SingBoxInstall", "docker")] }, approval with { CanonicalSubject = Guid.NewGuid().ToString("D") } })
        {
            returned = invalid;
            try { await client.ApproveAsync(plan, command, "approve", default); throw new Exception("Accepted changed scope"); }
            catch (InvalidDataException) { Check(true, "rejects retargeted approval identity, digest or capability"); }
        }
        path = TaskPlanningRoutes.Approval(approval.ApprovalId);
        returned = approval with { State = TaskApprovalState.Consumed, Revision = 2, RunId = queued.RunId, SessionIdentity = "historical-session" };
        Check((await client.ReadApprovalAsync(plan, approval.ApprovalId, default)).RunId == queued.RunId,
            "read-only approval lookup recovers an accepted run from a historical session without consuming authority");
        returned = approval with { ApprovalId = Guid.NewGuid() };
        try { await client.ReadApprovalAsync(plan, approval.ApprovalId, default); throw new Exception("Accepted another approval lookup"); }
        catch (InvalidDataException) { Check(true, "approval lookup cannot replace the requested approval identity"); }
        path = TaskPlanningRoutes.StartRun(plan.PlanId); returned = queued;
        Check((await client.StartAsync(plan, approval, "start", default)).RunId == queued.RunId, "validates queued run binding to the approved plan");
        returned = queued with { ApprovalId = Guid.NewGuid() };
        try { await client.StartAsync(plan, approval, "start", default); throw new Exception("Accepted another approval"); }
        catch (InvalidDataException) { Check(true, "start cannot switch approval identity"); }
        path = TaskPlanningRoutes.Run(queued.RunId); returned = queued with { RunId = Guid.NewGuid() };
        try { await client.ReadAsync(plan, queued.RunId, default); throw new Exception("Accepted another run"); }
        catch (InvalidDataException) { Check(true, "read cannot switch run identity"); }
        var active = queued with { State = TaskRunState.Running, Revision = 3, Steps = [queued.Steps[0] with
            { State = TaskStepRunState.Running, RequestDigest = new string('b', 64), DomainOperationId = Guid.NewGuid() }] };
        returned = active with { SessionIdentity = "another-session" };
        try { await client.RefreshAsync(plan, active, default); throw new Exception("Rebound tracked run"); }
        catch (InvalidDataException) { Check(true, "refresh preserves the tracked run's original session binding"); }
        path = TaskPlanningRoutes.Advance(queued.RunId); returned = active;
        Check((await client.AdvanceAsync(plan, queued, default)).Revision == 3, "accepts forward run revision and bound operation");
        var uncertain = active with { State = TaskRunState.NeedsRecovery, Revision = 4, ProblemCode = "task.domain_recovery_required",
            Steps = [active.Steps[0] with { State = TaskStepRunState.NeedsRecovery, ProblemCode = "task.domain_recovery_required" }] };
        var resolved = uncertain with { State = TaskRunState.Succeeded, Revision = 5, ProblemCode = null,
            Steps = [uncertain.Steps[0] with { State = TaskStepRunState.Succeeded, ProblemCode = null }] };
        path = TaskPlanningRoutes.RecheckRecovery(queued.RunId); returned = resolved;
        Check((await client.RecheckRecoveryAsync(plan, uncertain, default)).State == TaskRunState.Succeeded,
            "explicit recovery accepts a newer conclusive result for the original operation");
        foreach (var invalid in new[] { resolved with { Revision = 4 }, resolved with { Steps = [resolved.Steps[0] with { DomainOperationId = Guid.NewGuid() }] },
            active with { Revision = 5 }, queued with { Revision = 5 } })
        {
            returned = invalid;
            try { await client.RecheckRecoveryAsync(plan, uncertain, default); throw new Exception("Accepted forged recovery transition"); }
            catch (InvalidDataException) { Check(true, "recovery rejects same-revision result, operation replacement or renewed execution"); }
        }
        returned = resolved; path = TaskPlanningRoutes.Run(queued.RunId);
        Check((await client.RefreshAsync(plan, uncertain, default)).State == TaskRunState.Succeeded,
            "read-only refresh observes recovery resolved by another window");
        returned = uncertain with { Revision = 6 };
        try { await client.RefreshAsync(plan, resolved, default); throw new Exception("Reopened a resolved result"); }
        catch (InvalidDataException) { Check(true, "resolved success cannot return to recovery"); }
        path = TaskPlanningRoutes.Advance(queued.RunId);
        foreach (var invalid in new[] { active with { Revision = 1 }, active with { Steps = [active.Steps[0] with { IdempotencyKey = new string('c', 64) }] },
            active with { State = TaskRunState.Succeeded, Steps = [queued.Steps[0] with { State = TaskStepRunState.NoOp }] } })
        {
            returned = invalid;
            try { await client.AdvanceAsync(plan, queued, default); throw new Exception("Accepted invalid transition"); }
            catch (InvalidDataException) { Check(true, "rejects revision-free mutation, key replacement or invented write NoOp success"); }
        }
        path = TaskPlanningRoutes.CancelRun(active.RunId);
        var cancelling = active with { State = TaskRunState.Cancelling, Revision = 4, CancellationRequested = true }; returned = cancelling;
        Check((await client.CancelAsync(plan, active, default)).State == TaskRunState.Cancelling, "cancellation intent remains distinct from completed cancellation");
        returned = cancelling with { Steps = [active.Steps[0] with { DomainOperationId = Guid.NewGuid() }] };
        try { await client.CancelAsync(plan, active, default); throw new Exception("Changed original operation"); }
        catch (InvalidDataException) { Check(true, "cancel cannot replace the original domain operation"); }
        var beforeInvalidCommand = calls;
        var attempt = new TaskRecoveryAttempt(Guid.NewGuid(), uncertain.RunId, "change", uncertain.Steps[0].DomainOperationId!.Value,
            uncertain.Revision, TaskRecoveryAction.EmergencyStop, uncertain.CanonicalSubject, auth.Login.Id.ToString("D"), "host", "policy",
            "linux:0", "fixture", now.AddSeconds(3), now.AddMinutes(1), true, null);
        var recovery = new TaskRecoveryView(uncertain, new(true, attempt.OperationId, RuntimeChange.Uninstall,
            RelaxKonOS.Protocol.Installations.InstallationStage.Failed, true, "runtime.failed", "runtime.compensation_failed", null, null, new(true, true, false)), [attempt]);
        path = TaskPlanningRoutes.Recovery(uncertain.RunId); returned = recovery;
        Check((await client.ReadRecoveryAsync(plan, uncertain, default)).Attempts.Single().AttemptId == attempt.AttemptId,
            "recovery read preserves the original operation and persisted request evidence");
        foreach (var invalid in new[] { recovery with { Recovery = recovery.Recovery with { OperationId = Guid.NewGuid() } },
            recovery with { Attempts = [attempt, attempt] }, recovery with { Attempts = [attempt with { RunId = Guid.NewGuid() }] } })
        {
            returned = invalid;
            try { await client.ReadRecoveryAsync(plan, uncertain, default); throw new Exception("Accepted forged recovery details"); }
            catch (InvalidDataException) { Check(true, "recovery details reject replaced operation, duplicate or unrelated attempts"); }
        }
        var recoveryCommand = new TaskRecoveryRequest { ExpectedRevision = uncertain.Revision, OperationId = attempt.OperationId,
            Action = TaskRecoveryAction.EmergencyStop, Confirmed = true };
        returned = new TaskRecoveryResponse(uncertain, attempt);
        Check((await client.RecoverAsync(plan, uncertain, recoveryCommand, "recover", default)).Attempt.AttemptId == attempt.AttemptId,
            "explicit recovery accepts a response for the exact confirmed action");
        foreach (var invalid in new[] { attempt with { SessionIdentity = "other-session" }, attempt with { Action = TaskRecoveryAction.Close },
            attempt with { OperationId = Guid.NewGuid() }, attempt with { ExpectedRevision = uncertain.Revision - 1 } })
        {
            returned = new TaskRecoveryResponse(uncertain, invalid);
            try { await client.RecoverAsync(plan, uncertain, recoveryCommand, "recover", default); throw new Exception("Accepted substituted recovery command"); }
            catch (InvalidDataException) { Check(true, "recovery response binds current session, action, original operation and revision"); }
        }
        var beforeRecovery = calls;
        path = TaskPlanningRoutes.Evidence(uncertain.RunId);
        var selection = TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(plan.Semantics.Steps[0].Inputs).Selection;
        var snapshot = new RuntimeInstallationSnapshot(new(selection.InstallationId, "host", "sing-box", 8, RuntimeOwnership.Managed,
            new("linux", "amd64", null, null, RuntimeInstallMode.Archive, "systemd"), "previous", false), "1.14.3", null, null, null, null, null,
            new(attempt.OperationId, RuntimeSwitchOutcome.Interrupted, null));
        var observation = new TaskStepResultEvidence("change", attempt.OperationId, RelaxKonOS.Protocol.Installations.InstallationOperationState.Interrupted, false, snapshot);
        var savedEvidence = new TaskRunEvidence(uncertain, [observation]); returned = savedEvidence;
        Check((await client.ReadEvidenceAsync(plan, uncertain, default)).Observations.Single().Snapshot?.MeasuredVersion == "1.14.3",
            "saved evidence preserves historical measured facts for the original operation");
        foreach (var invalid in new[] { savedEvidence with { Observations = [observation with { OperationId = Guid.NewGuid() }] },
            savedEvidence with { Observations = [observation, observation] }, savedEvidence with { Observations = [observation with { RecoveryCheck = true }] },
            savedEvidence with { Observations = [observation with { Snapshot = snapshot with { Installation = snapshot.Installation with { HostId = "other" } } }] },
            savedEvidence with { Observations = [observation with { Snapshot = snapshot with { Receipt = snapshot.Receipt! with { OperationId = Guid.NewGuid() } } }] },
            savedEvidence with { Run = uncertain with { Revision = 3 } } })
        {
            returned = invalid;
            try { await client.ReadEvidenceAsync(plan, uncertain, default); throw new Exception("Accepted replaced historical evidence"); }
            catch (InvalidDataException) { Check(true, "evidence rejects operation, sequence, host, receipt and run continuity substitutions"); }
        }
        returned = savedEvidence with { Observations = [observation with { Snapshot = null }, observation with { RecoveryCheck = true }] };
        Check((await client.ReadEvidenceAsync(plan, uncertain, default)).Observations.Length == 2,
            "recovery evidence is additive and preserves the original missing observation");
        beforeRecovery = calls;
        try { await client.RecoverAsync(plan, uncertain, new() { ExpectedRevision = uncertain.Revision, OperationId = attempt.OperationId,
            Action = TaskRecoveryAction.Close, Confirmed = false }, "recover", default); throw new Exception("Sent unconfirmed recovery"); }
        catch (InvalidOperationException) { Check(calls == beforeRecovery, "unconfirmed recovery is rejected before transport"); }
        beforeInvalidCommand = calls;
        try { await client.ApproveAsync(plan, command, "", default); throw new Exception("Sent a missing idempotency key"); }
        catch (InvalidOperationException ex) { Check(ex.Message == "task.idempotency_required" && calls == beforeInvalidCommand, "missing idempotency key is rejected before transport"); }
        try { await client.ApproveAsync(plan, new ApproveTaskPlanRequest { PlanDigest = plan.PlanDigest, Confirmed = false }, "approve", default); throw new Exception("Sent unconfirmed approval"); }
        catch (InvalidOperationException ex) { Check(ex.Message == "task.confirmation_required" && calls == beforeInvalidCommand, "unconfirmed approval cannot send credentials or a command"); }
        var before = calls; auth.Server = "another-server";
        try { await client.ReadAsync(plan, queued.RunId, default); throw new Exception("Sent to changed server"); }
        catch (InvalidOperationException ex) { Check(ex.Message == "task.session_changed" && calls == before, "server switch is rejected before transport"); }
        auth.Server = "server"; auth.BaseUrl = "http://another-host/";
        try { await client.ReadAsync(plan, queued.RunId, default); throw new Exception("Sent to changed address"); }
        catch (InvalidOperationException ex) { Check(ex.Message == "task.session_changed" && calls == before, "base address change cannot redirect credentials"); }
        auth.BaseUrl = "http://localhost/";
        using var denied = new HttpClient(new Handler(_ => new(HttpStatusCode.Forbidden) { Content = new StringContent("sensitive authentication diagnostic") }));
        try { await new TaskExecutionClient(denied, session).ApproveAsync(plan, command, "denied", default); throw new Exception("Accepted denial"); }
        catch (HttpRequestException ex) { Check(ex.Message == "task.permission_denied", "raw error bodies do not reach the desktop"); }
        using var safeError = new HttpClient(new Handler(_ => new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { code = "task.run_revision_conflict", detail = "secret" }) }));
        try { await new TaskExecutionClient(safeError, session).AdvanceAsync(plan, active, default); throw new Exception("Accepted conflict"); }
        catch (HttpRequestException ex) { Check(ex.Message == "task.run_revision_conflict", "only allowlisted task errors reach callers"); }
        foreach (var body in new[] { "{\"runId\":\"x\",\"runId\":\"y\"}", "null", new string('x', 65537) })
        {
            using var malformed = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) }));
            try { await new TaskExecutionClient(malformed, session).ReadAsync(plan, queued.RunId, default); throw new Exception("Accepted malformed run"); }
            catch (InvalidDataException) { Check(true, "duplicate, null or oversized run response is rejected"); }
        }
        using var switched = new HttpClient(new Handler(_ =>
        {
            auth.Login = auth.Login with { Id = Guid.NewGuid() };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(queued, options: RelaxKonOSJsonOptions.Default) };
        }));
        try { await new TaskExecutionClient(switched, session).ReadAsync(plan, queued.RunId, default); throw new Exception("Accepted old-session reply"); }
        catch (InvalidOperationException ex) { Check(ex.Message == "task.session_changed", "session change while awaiting transport discards the reply"); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request)); }
}
