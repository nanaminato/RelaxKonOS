using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.TaskOrchestration;

internal static class TaskStepAuthorizationChecks
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        void Reject(Action action, string code)
        {
            try { action(); throw new Exception("Accepted invalid authority"); }
            catch (TaskApprovalException ex) { Check(ex.ProblemCode == code, code); }
        }
        var root = Path.Combine(Path.GetTempPath(), "task-stage-authority-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var plan = await new TaskPlanner(RuntimeChangePlanningActions.Create(new RuntimeChangeFixture()))
                .PlanAsync(new("host", "subject"), RuntimeChangePlanningChecks.Intent(RuntimeChange.Update));
            var clock = new Clock { Now = plan.CreatedAt.AddSeconds(1) };
            var store = new TaskPlanStore(Path.Combine(root, "plans.db"), new EphemeralDataProtectionProvider(), clock);
            store.Save(plan, "subject");
            var live = new TaskExecutionIdentity("subject", "session", "host", "policy");
            var approval = store.RecordApproval(plan.PlanId, plan.PlanDigest, new("subject", "session", "host", "policy",
                "linux:0", "fixture", clock.Now.AddMinutes(1)), "approve");
            var run = store.StartRun(plan.PlanId, approval.ApprovalId, plan.PlanDigest, live, "start");
            var dispatch = store.PrepareDispatch(run.RunId, "runtime", live, 1);
            var operation = Guid.NewGuid(); store.BindDomainOperation(run.RunId, "runtime", "subject", dispatch.RequestDigest, operation, 2);
            var identity = new ClaimsIdentity([new Claim("sub", "subject")], "fixture");
            var principal = new ClaimsPrincipal(identity);
            var policies = new Policies { Observation = new(live, clock.Now.AddSeconds(30)) };
            var approvers = new Approvers();
            var service = new TaskStepAuthorizationService(store, policies, approvers, clock);
            Reject(() => service.Bind(principal, run.RunId, "runtime", Guid.NewGuid(), dispatch.RequestDigest), "task.execution_authority_invalid");
            Reject(() => service.Bind(principal, run.RunId, "runtime", operation, new string('0', 64)), "task.execution_authority_invalid");
            var authority = service.Bind(principal, run.RunId, "runtime", operation, dispatch.RequestDigest);
            Check(authority.OperationId == operation && await authority.ValidateAsync(default) == clock.Now.AddSeconds(10)
                && approvers.Identity == "linux:0", "authority binds the operation and uses short stages with current approving-account membership");
            policies.Observation = new(live, clock.Now.AddSeconds(5));
            Check(await authority.ValidateAsync(default) == policies.Observation.ValidUntil, "stage grant cannot outlive current session/controller evidence");
            policies.Observation = new(live, clock.Now.AddSeconds(30));
            foreach (var changed in new[] { live with { SessionIdentity = "other" }, live with { HostId = "other" }, live with { PolicyRevision = "other" } })
            {
                policies.Observation = new(changed, clock.Now.AddSeconds(30));
                Reject(() => authority.ValidateAsync(default).GetAwaiter().GetResult(), "task.execution_authority_invalid");
            }
            policies.Observation = new(live, clock.Now.AddSeconds(30));
            approvers.Deny = true;
            Reject(() => authority.ValidateAsync(default).GetAwaiter().GetResult(), "task.approver_unavailable");
            approvers.Deny = false;
            var retained = identity.FindFirst("sub")!; identity.RemoveClaim(retained); identity.AddClaim(new("sub", "other"));
            Check(await authority.ValidateAsync(default) > clock.Now, "mutable caller claims cannot rewrite the retained execution identity");
            identity.RemoveClaim(identity.FindFirst("sub")!); identity.AddClaim(new("sub", "subject"));
            var grant = await TaskRuntimeAuthorizationBridge.ReadAsync(authority, _ => Task.FromResult(new SingBoxCurrentAuthorization(null, clock.Now.AddMinutes(1))), default);
            Check(grant.ValidUntil == clock.Now.AddSeconds(10), "published credential grant is clipped to task stage authority");
            await using var callback = new RuntimeAuthorizationServer<SingBoxCurrentAuthorization>(operation,
                token => TaskRuntimeAuthorizationBridge.ReadAsync(authority,
                    _ => Task.FromResult(new SingBoxCurrentAuthorization(null, clock.Now.AddMinutes(1))), token));
            using var callbackDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Check((await RuntimeAuthorizationClient.ReadAsync<SingBoxCurrentAuthorization>(callback.Channel, operation,
                callbackDeadline.Token)).ValidUntil == clock.Now.AddSeconds(10), "real local callback channel returns only operation-bound, clipped task authority");
            policies.Observation = new(live with { PolicyRevision = "revoked" }, clock.Now.AddSeconds(30));
            try
            {
                await RuntimeAuthorizationClient.ReadAsync<SingBoxCurrentAuthorization>(callback.Channel, operation, callbackDeadline.Token);
                throw new Exception("Local channel reused revoked authority");
            }
            catch (RuntimePolicyException) { Check(true, "the next authenticated local challenge rejects revoked task policy"); }
            policies.Observation = new(live, clock.Now.AddSeconds(30));
            try
            {
                await TaskRuntimeAuthorizationBridge.ReadAsync(authority, _ =>
                {
                    policies.Observation = new(live with { PolicyRevision = "revoked" }, clock.Now.AddSeconds(30));
                    return Task.FromResult(new SingBoxCurrentAuthorization(null, clock.Now.AddMinutes(1)));
                }, default);
                throw new Exception("Accepted revocation while reading published material");
            }
            catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "runtime.authorization_unavailable", "revocation during published-state reading prevents the callback grant"); }
            policies.Observation = new(live, clock.Now.AddSeconds(30));
            var savedNow = clock.Now; clock.Now = clock.Now.AddMinutes(2);
            Reject(() => authority.ValidateAsync(default).GetAwaiter().GetResult(), "task.execution_authority_expired"); clock.Now = savedNow;
            var runtime = new SingBoxRuntimeService(null!, null!, null!, null!, null!);
            var request = TaskPlanJson.ReadInputs<ManagedRuntimeInstallationRequest>(dispatch.Request);
            foreach (var altered in new[] { request.Plan with { PreviousReleaseId = "other" },
                request.Plan with { Request = request.Plan.Request with { IdempotencyKey = "different" } } })
            {
                try { await runtime.ExecuteTaskAsync(operation, altered, authority, default); throw new Exception("Dispatched altered request"); }
                catch (RuntimeLifecycleException ex) { Check(ex.ProblemCode == "runtime.authorization_unavailable", "task runtime entry rejects changed frozen request before using transport"); }
            }
            approvers.During = () => store.RevokeApproval(approval.ApprovalId, "subject");
            Reject(() => authority.ValidateAsync(default).GetAwaiter().GetResult(), "task.execution_authority_invalid");
            approvers.During = null;
            Reject(() => authority.ValidateAsync(default).GetAwaiter().GetResult(), "task.execution_authority_invalid");
        }
        finally { Directory.Delete(root, true); }
        return count;
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Policies : ITaskExecutionPolicyReader
    {
        public TaskExecutionPolicyObservation Observation = null!;
        public TaskExecutionPolicyObservation Read(ClaimsPrincipal principal, string boundHostId) => Observation;
    }
    private sealed class Approvers : ITaskApproverPolicyReader
    {
        public string? Identity; public bool Deny; public Action? During;
        public void Validate(string canonicalIdentity)
        { Identity = canonicalIdentity; if (Deny) throw new TaskApprovalException("task.approver_unavailable"); During?.Invoke(); }
    }
}
