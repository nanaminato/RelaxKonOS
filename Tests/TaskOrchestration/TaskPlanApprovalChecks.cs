using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.TaskOrchestration;

internal static class TaskPlanApprovalChecks
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; }
        var root = Path.Combine(Path.GetTempPath(), "task-approval-composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var domain = new RuntimeChangeFixture();
            var planning = new Planning(domain);
            var plan = await planning.PlanAsync("subject", RuntimeChangePlanningChecks.Intent(RuntimeChange.Update), default);
            var revalidator = new RuntimeTaskPlanRevalidator(planning);
            domain.TransformDependency = item => item.Kind == RuntimeDependencyKind.DiskSpace
                ? item with { AvailableBytes = item.AvailableBytes + 1024 } : item;
            await revalidator.ValidateAsync(plan, default);
            Check(TaskPlanCanonicalizer.Digest(plan.Semantics) == plan.PlanDigest,
                "fresh sufficient disk evidence neither invalidates nor modifies the frozen plan");
            domain.TransformDependency = item => item.Kind == RuntimeDependencyKind.DiskSpace
                ? item with { State = RuntimeDependencyState.Missing, AvailableBytes = 0 } : item;
            try { await revalidator.ValidateAsync(plan, default); throw new Exception("Accepted insufficient disk"); }
            catch (TaskPlanningException ex) { Check(ex.ProblemCode == "task.domain_plan_mismatch", "insufficient disk is never normalized into sufficient evidence"); }
            domain.TransformDependency = null;
            var originalInstallation = domain.Installation;
            domain.Installation = originalInstallation with { ConfigurationIdentity = "changed-config" };
            try { await revalidator.ValidateAsync(plan, default); throw new Exception("Accepted changed config"); }
            catch (TaskApprovalException ex) { Check(ex.ProblemCode == "task.plan_changed", "published configuration identity is part of revalidation"); }
            domain.Installation = originalInstallation;
            domain.Plans = 1;
            var store = new TaskPlanStore(Path.Combine(root, "plans.db"), new EphemeralDataProtectionProvider());
            store.Save(plan, "subject");
            var policy = new Policy(); var admin = new Administrator();
            var service = new TaskPlanApprovalService(store, policy, new RuntimeTaskPlanRevalidator(planning), admin);
            var principal = new ClaimsPrincipal();
            async Task Reject(Func<Task> action, string code)
            {
                try { await action(); throw new Exception("Accepted invalid approval"); }
                catch (TaskApprovalException ex) { Check(ex.ProblemCode == code, code); }
            }
            Task<TaskPlanApproval> Approve(bool confirmed = true, string? digest = null) => service.ApproveAsync(principal,
                plan.PlanId, digest ?? plan.PlanDigest, confirmed, Guid.NewGuid().ToString("N"), "display-name", "fixture-secret", default);
            await Reject(() => Approve(false), "task.confirmation_required");
            Check(policy.Reads == 0 && admin.Calls == 0, "confirmation is required before identity observation or authentication");
            policy.Deny = true;
            await Reject(() => Approve(), "task.permission_denied");
            Check(admin.Calls == 0 && domain.Plans == 1, "business denial occurs before administrator authentication and domain replanning");
            policy.Deny = false;
            await Reject(() => Approve(digest: new string('0', 64)), "task.plan_digest_conflict");
            var approved = await Approve();
            Check(approved.ApproverIdentity == "linux:0" && approved.AuthenticationMethod == "fixture-authentication"
                && approved.CanonicalSubject == "subject" && approved.StartBefore <= policy.Until,
                "approval records authenticated UID rather than the administrator display-name or password");
            Check(admin.Calls == 1 && admin.Current == "alice" && policy.Reads == 4,
                "standard user is authenticated once and policy is observed before and after authentication");
            admin.Result = new(true, "", "fixture", null);
            await Reject(() => Approve(), "task.administrator_authentication_failed");
            admin.Result = new(true, "", "fixture", "windows:S-1-5-18");
            await Reject(() => Approve(), "task.administrator_authentication_failed");
            admin.Result = new(false, "wrong-password", "none", null);
            await Reject(() => Approve(), "task.administrator_authentication_failed");
            admin.Result = new(true, "", "fixture-authentication", "linux:0");
            admin.During = () => policy.Revision = "revoked-policy";
            await Reject(() => Approve(), "task.policy_changed");
            policy.Revision = "policy"; admin.During = () => policy.Deny = true;
            await Reject(() => Approve(), "task.permission_denied");
            policy.Deny = false;
            var release = domain.Release;
            admin.During = () => domain.Release = release with { Artifact = release.Artifact with { Sha256 = new string('c', 64) } };
            await Reject(() => Approve(), "task.plan_changed");
            domain.Release = release; admin.During = null;
            policy.Installation = policy.Installation with { PendingOperationId = Guid.NewGuid() };
            await Reject(() => Approve(), "task.plan_changed");
            policy.Installation = new RuntimeChangeFixture().Installation;
            policy.Privilege = HostAccountPrivilege.HostRoot;
            var calls = admin.Calls;
            await Reject(() => Approve(false), "task.confirmation_required");
            var automatic = await Approve();
            Check(admin.Calls == calls && automatic.ApproverIdentity == "linux:1000"
                && automatic.AuthenticationMethod == "current-host-root", "current host administrator still confirms and uses canonical requester identity");
            policy.Privilege = HostAccountPrivilege.StandardUser;
            admin.During = () => store.Invalidate(plan.PlanId, "subject", plan.PlanDigest, 1);
            try { await Approve(); throw new Exception("Accepted invalidated plan"); }
            catch (TaskPlanStoreException ex) { Check(ex.ProblemCode == "task.plan_expired", "final transaction rejects plan invalidated during authentication"); }
        }
        finally { Directory.Delete(root, true); }
        return count;
    }
    private sealed class Planning(RuntimeChangeFixture domain) : IRuntimeTaskChangePlanningService
    {
        public Task<TaskPlan> PlanAsync(string subject, TaskPlanIntent intent, CancellationToken ct) =>
            new TaskPlanner(RuntimeChangePlanningActions.Create(domain)).PlanAsync(new("host", subject), intent, ct);
    }
    private sealed class Policy : ITaskApprovalPolicyReader
    {
        public int Reads; public bool Deny; public string Revision = "policy";
        public DateTimeOffset Until = DateTimeOffset.UtcNow.AddMinutes(1);
        public HostAccountPrivilege Privilege = HostAccountPrivilege.StandardUser;
        public RuntimeInstallationSnapshot Installation = new RuntimeChangeFixture().Installation;
        public Task<TaskApprovalObservation> ReadAsync(ClaimsPrincipal principal, CancellationToken ct)
        {
            Reads++; if (Deny) throw new TaskApprovalException("task.permission_denied");
            return Task.FromResult(new TaskApprovalObservation(new("subject", "session", "host", Revision),
                new("1000", "alice", HostPlatformKind.Linux, "Alice", null), Privilege, Until, Installation));
        }
    }
    private sealed class Administrator : IHostAdministratorAuthenticator
    {
        public int Calls; public string? Current; public Action? During;
        public HostAdministratorAuthenticationResult Result = new(true, "", "fixture-authentication", "linux:0");
        public HostAdministratorAuthenticationResult Authenticate(string currentUsername, string? administratorUsername, string? password)
        { Calls++; Current = currentUsername; During?.Invoke(); return Result; }
    }
}
