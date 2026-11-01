using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Startup;
using System.Text.Json.Nodes;

internal static class ModulePrivilegeChecks
{
    private const string PackageSource = "host-approved-mysql";
    private const string PackageName = "mysql-server";
    private const string PackageVersion = "8.0.39-0ubuntu0.24.04.2";
    private const string PackageSha = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string DependencySha = "2222222222222222222222222222222222222222222222222222222222222222";

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Current = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Current;
    }
    /// <summary>Records the closed Helper requests the Broker emits and answers with a scripted outcome.</summary>
    private sealed class RecordingTransport : IPrivilegedOperationTransport
    {
        internal readonly List<PrivilegedOperationRequest> Requests = [];
        internal PrivilegedOperationResult Result = new(true);
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await ModulePrivilegeConsentChecks.RunAsync(check);
        var root = Path.Combine(Path.GetTempPath(), "module-privileges-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock();
            var identity = new ModulePrivilegeIdentity("fixture", "1.0.0", "publisher", new string('a', 64), new string('b', 64),
                "revision-1", [ModuleCapabilityFixtures.Service("fixture.service", "restart"),
                    ModuleCapabilityFixtures.Package(PackageSource, PackageName, "install", "remove"),
                    new PackageManageCapability
                    {
                        Platforms = [ModuleCapabilityFixtures.Platform],
                        Manager = "windows-msi",
                        SourceRef = "host-approved-msi",
                        Packages = ["fixture-msi"],
                        Versions = new("1.0.0", "2.0.0"),
                        Actions = ["install"],
                    }], ["fixture.service"]);
            var trusted = true;
            static Task<ModulePrivilegeAdministrator?> Verify() => Task.FromResult<ModulePrivilegeAdministrator?>(new("linux:1000", "linux-pam-administrator"));
            ModulePrivilegeIdentity Resolve(string module) => trusted && module == identity.ModuleId
                ? identity : throw new ModulePrivilegeException("module.privilege_denied", 403);
            var store = new ModulePrivilegePlanStore(root, Resolve, clock);
            Task<ModulePrivilegePrepared> PrepareRequest(ModulePrivilegeRequest request) => store.PrepareAsync("fixture", "session-1", request, "test-user");
            ModulePrivilegeRequest Request(string key, string target = "fixture.service", string action = "restart") =>
                new(ModuleCapabilityCatalog.ServiceManage, action, target, "Restart the fixture service", key);
            async Task Reject(Func<Task> action, string code, string message)
            {
                try { await action(); }
                catch (ModulePrivilegeException error) when (error.Code == "module.privilege_" + code)
                { check(true, message); return; }
                throw new Exception("Expected module privilege rejection: " + message);
            }
            async Task<ModulePrivilegePlan> Prepare(string key)
            {
                var prepared = await PrepareRequest(Request(key));
                return await store.ReadForUserAsync(prepared.PlanId, "session-1");
            }
            static ModulePrivilegeApproval Approval(ModulePrivilegePlan plan) => new(plan.PlanHash, plan.ConfirmationToken);
            static ModulePackageResolution FrozenPackage(string version = PackageVersion) => new(
                PackageName, version, "amd64", "noble-updates|main|amd64", "http://archive.ubuntu.com/ubuntu",
                "noble-updates", "main",
                "http://archive.ubuntu.com/ubuntu/pool/main/m/mysql-server/mysql-server_" + version + "_all.deb",
                PackageSha, 2048,
                [new ModulePackageArtifact("mysql-server-8.0_" + version + "_amd64.deb",
                    "http://archive.ubuntu.com/ubuntu/pool/main/m/mysql-server-8.0/mysql-server-8.0_" + version + "_amd64.deb",
                    4096, DependencySha)]);
            var executions = 0;
            Task<bool> Execute(Guid id, ModulePrivilegeExecution execution)
            {
                check(id != Guid.Empty && execution.Request.Kind == ModuleCapabilityCatalog.ServiceManage
                    && execution.Request.Action == "restart" && execution.Request.Target == "fixture.service"
                    && execution.Package is null && execution.File is null && execution.Data is null && execution.Content is null,
                    "Broker executes the exact frozen typed action");
                executions++;
                return Task.FromResult(true);
            }
            var plan = await Prepare("first");
            check(plan.State == "AwaitingConfirmation" && plan.ConfirmationToken.Length == 64 && executions == 0,
                "Broker prepares without executing and issues a Core-only confirmation token");
            var same = await PrepareRequest(Request("first"));
            check(same.PlanId == plan.PlanId, "Broker idempotency returns the original plan");
            await Reject(() => PrepareRequest(Request("first") with { Reason = "changed" }),
                "idempotency_conflict", "Broker rejects idempotency key reuse with changed content");
            await Reject(() => PrepareRequest(Request("undeclared", "other.service")),
                "not_declared", "Broker rejects undeclared targets");
            await Reject(() => PrepareRequest(Request("undeclared-action", action: "stop")),
                "not_declared", "Broker rejects actions outside the declared service scope");
            await Reject(() => PrepareRequest(new(ModuleCapabilityCatalog.PackageManage, "install", "mysql-server",
                "Install a package", "package")),
                "unsupported", "Broker refuses a package plan when no Helper resolution adapter is available");
            await Reject(() => PrepareRequest(new(ModuleCapabilityCatalog.ServiceManage, "restart", "fixture.service",
                "Use a secret", "secret", [new("database-password", "mysql", "read")])),
                "unsupported", "Broker rejects secret references until a delivery adapter exists");
            await Reject(() => PrepareRequest(new("shell", "restart", "fixture.service", "Run a command", "shell")),
                "unsupported", "Broker rejects arbitrary command kinds");
            await Reject(() => store.ReadForUserAsync(plan.PlanId, "other-session"), "denied", "Broker binds plans to the initiating session");
            await Reject(() => store.GetForModuleAsync(plan.PlanId, "other-module"), "denied", "Broker prevents cross-module result reads");
            await Reject(() => store.ApproveAsync(plan.PlanId, "session-1", Approval(plan) with { PlanHash = new string('0', 64) },
                Verify, Execute), "approval_invalid", "Broker rejects a changed plan hash");
            await Reject(() => store.ApproveAsync(plan.PlanId, "session-1", Approval(plan) with { ConfirmationToken = new string('0', 64) },
                Verify, Execute), "approval_invalid", "Broker rejects a forged confirmation token");
            await Reject(() => store.ApproveAsync(plan.PlanId, "session-1", Approval(plan), () => Task.FromResult<ModulePrivilegeAdministrator?>(null), Execute),
                "admin_required", "Broker rejects failed administrator verification without execution");
            var success = await store.ApproveAsync(plan.PlanId, "session-1", Approval(plan), Verify, Execute);
            check(success.State == "Succeeded" && executions == 1, "Broker executes once after explicit confirmation and verification");
            await Reject(() => store.ApproveAsync(plan.PlanId, "session-1", Approval(plan), Verify, Execute),
                "approval_invalid", "Broker rejects approval replay");

            // DC-02: a package plan freezes the Helper-resolved identity rather than a package name.
            var packageQueries = 0;
            ModulePackageResolution? packageResolution = FrozenPackage();
            Task<ModulePackageResolution?> Query(ModulePackageQuery query)
            {
                packageQueries++;
                return Task.FromResult(packageResolution is { } frozen && query.SourceRef == PackageSource
                    && query.Package == PackageName ? frozen : null);
            }
            Task<ModulePrivilegePrepared> PreparePackageRaw(string key, string action = "install",
                Func<ModulePackageQuery, Task<ModulePackageResolution?>>? query = null) =>
                store.PrepareAsync("fixture", "session-1",
                    new(ModuleCapabilityCatalog.PackageManage, action, PackageName, "Install the fixture package", key),
                    "test-user", query ?? Query);
            async Task<ModulePrivilegePlan> PreparePackage(string key, string action = "install",
                Func<ModulePackageQuery, Task<ModulePackageResolution?>>? query = null) =>
                await store.ReadForUserAsync((await PreparePackageRaw(key, action, query)).PlanId, "session-1");
            check(packageQueries == 0, "Read-only package resolution runs only for a package.manage plan");
            var packagePlan = await PreparePackage("package");
            check(packagePlan.Package is { } frozenView
                && frozenView.SourceRef == PackageSource && frozenView.Package == PackageName
                && frozenView.Version == PackageVersion && frozenView.Architecture == "amd64"
                && frozenView.Repository == "http://archive.ubuntu.com/ubuntu/noble-updates/main"
                && frozenView.ArtifactFileName == "mysql-server_" + PackageVersion + "_all.deb"
                && frozenView.Sha256 == PackageSha && frozenView.Bytes == 2048
                && frozenView.Dependencies.Length == 1 && frozenView.Dependencies[0].Sha256 == DependencySha,
                "Host freezes the exact version, architecture, repository, artifact and dependency closure into the plan");
            check(packageQueries == 1 && packagePlan.State == "AwaitingConfirmation" && packagePlan.ConfirmationToken.Length == 64
                && packagePlan.Warnings.Any(line => line.Contains("Maintainer scripts", StringComparison.Ordinal))
                && packagePlan.Warnings.Any(line => line.Contains("Dependent services", StringComparison.Ordinal))
                && packagePlan.Warnings.Any(line => line.Contains("no general rollback", StringComparison.Ordinal))
                && packagePlan.Effects.Contains("package-install", StringComparer.Ordinal),
                "package plan shows maintainer-script, service-impact and no-rollback warnings before confirmation");
            await Reject(() => PreparePackageRaw("package-no-resolution", query: _ => Task.FromResult<ModulePackageResolution?>(null)),
                "unsupported", "Broker refuses a package plan when the Helper cannot resolve the declared source");
            packageResolution = FrozenPackage("9.1.0");
            await Reject(() => PreparePackageRaw("package-outside-interval"), "not_declared",
                "Broker refuses a resolved version outside the declared interval");
            packageResolution = FrozenPackage() with { Package = "nginx" };
            await Reject(() => PreparePackageRaw("package-other-target"), "unsupported",
                "Broker refuses a Helper answer that names another package");
            packageResolution = FrozenPackage() with
            {
                Dependencies = [new ModulePackageArtifact("../escape.deb", "http://archive.ubuntu.com/ubuntu/x.deb", 1024, DependencySha)],
            };
            await Reject(() => PreparePackageRaw("package-unusable-identity"), "unsupported",
                "Broker refuses a frozen identity with an unusable dependency");
            packageResolution = FrozenPackage();
            await Reject(() => PreparePackageRaw("package-undeclared-action", "repair"), "not_declared",
                "Broker refuses a package action outside the declaration");
            await Reject(() => store.PrepareAsync("fixture", "session-1",
                    new(ModuleCapabilityCatalog.PackageManage, "install", "fixture-msi", "Run a Windows installer", "package-msi"),
                    "test-user", Query),
                "unsupported", "windows-msi package declarations stay unsupported until their adapter exists");
            var transport = new RecordingTransport();
            var packageApproved = await store.ApproveAsync(packagePlan.PlanId, "session-1", Approval(packagePlan), Verify,
                (operationId, execution) => CoreModulePrivilegeBroker.ExecuteCapabilityAsync(transport, operationId, execution));
            var applied = transport.Requests.Single().ModulePackageApply;
            check(packageApproved.State == "Succeeded" && applied is not null
                && transport.Requests.Single().Operation == PrivilegedOperationKind.ModulePackageApply
                && applied.Action == ModulePackageTransactionAction.Install && applied.SourceRef == PackageSource
                && applied.Target.Version == PackageVersion && applied.Target.Sha256 == PackageSha
                && !applied.RemoveConfiguration,
                "confirmed package plan sends only the frozen source reference and identity as ModulePackageApply");
            var driftPlan = await PreparePackage("package-drift");
            transport.Result = new(false, 65, ProblemCode: PrivilegedProblemCode.PlanChanged);
            await Reject(() => store.ApproveAsync(driftPlan.PlanId, "session-1", Approval(driftPlan), Verify,
                (operationId, execution) => CoreModulePrivilegeBroker.ExecuteCapabilityAsync(transport, operationId, execution)),
                "plan_changed", "Helper re-resolution drift is reported as a changed plan");
            var driftStatus = await store.GetForModuleAsync(driftPlan.PlanId, "fixture");
            check(driftStatus.State == "Failed" && driftStatus.ProblemCode == "module.privilege_plan_changed",
                "Helper re-resolution drift fails the confirmed plan instead of becoming an unknown retry");
            var lostPlan = await PreparePackage("package-lost");
            transport.Result = new(false, 69, ProblemCode: PrivilegedProblemCode.HelperUnavailable);
            var lost = await store.ApproveAsync(lostPlan.PlanId, "session-1", Approval(lostPlan), Verify,
                (operationId, execution) => CoreModulePrivilegeBroker.ExecuteCapabilityAsync(transport, operationId, execution));
            check(lost.State == "RecoveryRequired",
                "a lost Helper response after a package transaction stays unknown and never becomes success");
            transport.Result = new(true);
            await Reject(() => CoreModulePrivilegeBroker.ExecuteCapabilityAsync(transport, Guid.NewGuid(),
                new ModulePrivilegeExecution(new(ModuleCapabilityCatalog.PackageManage, "install", PackageName,
                    "No frozen identity", "unfrozen"), null, null, null, null)),
                "unsupported", "package execution without a frozen identity fails closed");
            var expired = await Prepare("expired");
            clock.Current = clock.Current.AddMinutes(6);
            await Reject(() => store.ApproveAsync(expired.PlanId, "session-1", Approval(expired), Verify, Execute),
                "plan_expired", "Broker rejects expired plans");
            var drifted = await Prepare("drifted");
            identity = identity with { Version = "2.0.0", ArtifactSha256 = new string('c', 64) };
            await Reject(() => store.ApproveAsync(drifted.PlanId, "session-1", Approval(drifted), Verify, Execute),
                "plan_changed", "Broker invalidates old plans after an update or policy change");
            var revoked = await Prepare("revoked");
            trusted = false;
            await Reject(() => store.ApproveAsync(revoked.PlanId, "session-1", Approval(revoked), Verify, Execute),
                "denied", "Broker rechecks revoked trust immediately before approval");
            trusted = true;
            var cancelled = await Prepare("cancelled");
            check((await store.CancelAsync(cancelled.PlanId, "session-1")).State == "Cancelled", "Broker persists user refusal");
            await Reject(() => store.ApproveAsync(cancelled.PlanId, "session-1", Approval(cancelled), Verify, Execute),
                "approval_invalid", "Broker cannot approve a refused plan");
            var concurrent = await Prepare("concurrent");
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                try { return (await store.ApproveAsync(concurrent.PlanId, "session-1", Approval(concurrent), Verify, Execute)).State; }
                catch (ModulePrivilegeException error) { return error.Code; }
            }));
            check(results.Count(s => s == "Succeeded") == 1 && executions == 2, "Broker serializes concurrent approvals and executes only once");
            var interrupted = await Prepare("interrupted");
            var unknown = await store.ApproveAsync(interrupted.PlanId, "session-1", Approval(interrupted), Verify,
                (_, _) => throw new IOException("lost Helper response"));
            check(unknown.State == "RecoveryRequired", "Broker treats a lost Helper response as unknown, without replay");
            var reopened = new ModulePrivilegePlanStore(root, Resolve, clock);
            check((await reopened.GetForModuleAsync(plan.PlanId, "fixture")).State == "Succeeded"
                && (await reopened.GetForModuleAsync(interrupted.PlanId, "fixture")).State == "RecoveryRequired",
                "Broker retains original success and recovery facts across restart");
            var reopenedPackage = await reopened.ReadForUserAsync(packagePlan.PlanId, "session-1");
            check(reopenedPackage.Package is { } reopenedView && reopenedView.Sha256 == PackageSha
                && reopenedPackage.PlanHash == packagePlan.PlanHash,
                "the frozen package identity survives restart with the same confirmed plan hash");
            var originalUnknown = await reopened.PrepareAsync("fixture", "session-1", Request("interrupted"), "test-user");
            check(originalUnknown.PlanId == interrupted.PlanId && originalUnknown.State == "RecoveryRequired",
                "Broker idempotent prepare retains an unknown result instead of creating a retry");
            var recoveryView = await reopened.ReadForUserAsync(interrupted.PlanId, "session-1");
            check(recoveryView.ConfirmationToken == "", "Broker does not issue confirmation tokens for unknown operations");
            await Reject(() => reopened.ApproveAsync(interrupted.PlanId, "session-1", Approval(interrupted),
                Verify, Execute), "approval_invalid", "Broker rejects replay after a lost Helper response and restart");
            var crash = await Prepare("crash-before-response");
            await store.ApproveAsync(crash.PlanId, "session-1", Approval(crash), Verify, async (_, _) =>
            {
                // Reopen while the durable record is Executing, before any completion response exists.
                var recovering = new ModulePrivilegePlanStore(root, Resolve, clock);
                check((await recovering.GetForModuleAsync(crash.PlanId, "fixture")).State == "RecoveryRequired",
                    "Broker recovers a durable Executing record as unknown before receiving any Helper result");
                await Reject(() => recovering.ApproveAsync(crash.PlanId, "session-1", Approval(crash),
                    Verify, Execute), "approval_invalid", "Broker recovery never reuses an in-flight confirmation");
                throw new IOException("simulated process interruption");
            });
            check(executions == 2, "Broker queries and restart never replay privileged actions");
            var persisted = Path.Combine(root, "data", "module-management", "privilege-plans.json");
            var original = File.ReadAllText(persisted);
            var persistedDocument = JsonNode.Parse(original)!;
            var records = persistedDocument["plans"]!.AsArray();
            var approvedAudit = records.Single(p => p!["id"]!.GetValue<Guid>() == plan.PlanId)!["audit"]!;
            check(approvedAudit["initiatingUser"]!.GetValue<string>() == "test-user"
                && approvedAudit["approvingUser"]!.GetValue<string>() == "test-user"
                && approvedAudit["administrator"]!["identity"]!.GetValue<string>() == "linux:1000"
                && approvedAudit["administrator"]!["authenticationMethod"]!.GetValue<string>() == "linux-pam-administrator"
                && approvedAudit["confirmedAt"] is not null && approvedAudit["attemptCompletedAt"] is not null,
                "Broker durably records initiating and approving users, actual administrator identity and attempt timestamps");
            var unknownAudit = records.Single(p => p!["id"]!.GetValue<Guid>() == interrupted.PlanId)!["audit"]!;
            check(unknownAudit["administrator"]!["identity"]!.GetValue<string>() == "linux:1000"
                && unknownAudit["confirmedAt"] is not null && unknownAudit["attemptCompletedAt"] is not null,
                "Broker retains the verified administrator and attempt evidence when the Helper response is unknown");
            var packaged = records.Single(p => p!["id"]!.GetValue<Guid>() == packagePlan.PlanId)!;
            check(persistedDocument["schemaVersion"]!.GetValue<int>() == 6
                && packaged["package"]!["sourceRef"]!.GetValue<string>() == PackageSource
                && packaged["package"]!["resolution"]!["sha256"]!.GetValue<string>() == PackageSha
                && packaged["packageHash"]!.GetValue<string>() is { Length: 64 },
                "package plan store is schemaVersion 6 and persists the frozen identity under its own hash");
            void RejectCorruption(Action<JsonObject> mutate, string message)
            {
                var document = JsonNode.Parse(original)!.AsObject();
                mutate(document);
                File.WriteAllText(persisted, document.ToJsonString());
                try
                {
                    _ = new ModulePrivilegePlanStore(root, Resolve, clock);
                    throw new Exception("Expected persisted plan corruption rejection");
                }
                catch (ModulePrivilegeException error) when (error.Code == "module.privilege_recovery_required") { check(true, message); }
                finally { File.WriteAllText(persisted, original); }
            }
            void RejectPackageCorruption(Action<JsonObject> mutate, string message)
            {
                var document = JsonNode.Parse(original)!.AsObject();
                var packageNode = document["plans"]!.AsArray()
                    .Single(node => node!["id"]!.GetValue<Guid>() == packagePlan.PlanId)!.AsObject();
                mutate(packageNode);
                File.WriteAllText(persisted, document.ToJsonString());
                try
                {
                    _ = new ModulePrivilegePlanStore(root, Resolve, clock);
                    throw new Exception("Expected persisted package identity rejection");
                }
                catch (ModulePrivilegeException error) when (error.Code == "module.privilege_recovery_required") { check(true, message); }
                finally { File.WriteAllText(persisted, original); }
            }
            RejectCorruption(d => d["plans"]![0]!["request"]!["target"] = "different.service",
                "Broker rejects a persisted request changed without its frozen hash");
            RejectCorruption(d => d["plans"]![0]!["identity"]!["version"] = "9.0.0",
                "Broker rejects a persisted module identity changed without its frozen hash");
            RejectCorruption(d => d["plans"]![0]!["hash"] = new string('0', 64),
                "Broker rechecks the composite plan hash on recovery");
            RejectCorruption(d => d["plans"]![0]!["state"] = "UnknownState",
                "Broker rejects an unrecognized persisted lifecycle state");
            RejectCorruption(d => d["plans"]![0]!["tokenHash"] = new string('a', 64),
                "Broker rejects a persisted confirmation token on a completed operation");
            RejectCorruption(d => d["plans"] = null, "Broker rejects a null persisted plan collection");
            RejectCorruption(d => d["plans"]![0]!["audit"]!["initiatingUser"] = "changed-user",
                "Broker binds the initiating audit user into the frozen plan hash");
            RejectCorruption(d => d["plans"]![0]!["audit"]!["approvingUser"] = "different-user",
                "Broker rejects a persisted approval attributed to another user");
            RejectCorruption(d => d["plans"]![0]!["audit"]!["administrator"] = null,
                "Broker rejects a completed action missing its verified administrator evidence");
            RejectCorruption(d => d["plans"]![0]!["package"] = JsonNode.Parse(packaged["package"]!.ToJsonString()),
                "Broker rejects a frozen package identity attached to a service plan");
            RejectPackageCorruption(node => node["package"]!["resolution"]!["version"] = "8.0.40-0ubuntu0.24.04.1",
                "Broker rejects a frozen package version changed without the plan hash");
            RejectPackageCorruption(node => node["package"] = null,
                "Broker rejects a package plan whose frozen identity disappeared");
            RejectCorruption(d => d["schemaVersion"] = 5, "Broker rejects the previous plan store format");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
