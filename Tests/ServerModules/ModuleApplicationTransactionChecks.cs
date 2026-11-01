extern alias modulehelper;

using System.Text;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Startup;
using System.Text.Json.Nodes;

internal static class ModuleApplicationTransactionChecks
{
    private const string Revision = "11111111111111111111111111111111";
    private const string NextRevision = "22222222222222222222222222222222";

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Current = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private sealed class RecordingTransport : IPrivilegedOperationTransport
    {
        internal readonly List<PrivilegedOperationRequest> Requests = [];
        internal PrivilegedOperationResult Result = new(true);
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        ValidateHelperPolicy(check);
        var root = Path.Combine(Path.GetTempPath(), "module-applications-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock();
            var identity = new ModulePrivilegeIdentity("fixture", "1.0.0", "publisher", new string('a', 64),
                new string('b', 64), "revision-1",
                [
                    new ApplicationManageCapability
                    {
                        Platforms = ["linux"], Protocols = ["mysql-admin"], Instances = ["mysql"],
                        Actions = ["query", "rotate-credential"],
                    },
                    new SecretReferenceCapability
                    {
                        Platforms = ["linux"],
                        Secrets = [new("database-admin", "mysql", ["read", "rotate"])],
                    },
                ], []);
            var trusted = true;
            ModulePrivilegeIdentity Resolve(string module) => trusted && module == identity.ModuleId
                ? identity : throw new ModulePrivilegeException("module.privilege_denied", 403);
            var store = new ModulePrivilegePlanStore(root, Resolve, clock);
            static Task<ModulePrivilegeAdministrator?> Verify() =>
                Task.FromResult<ModulePrivilegeAdministrator?>(new("linux:1000", "linux-pam-administrator"));
            static ModulePrivilegeApproval Approval(ModulePrivilegePlan plan) => new(plan.PlanHash, plan.ConfirmationToken);
            async Task Reject(Func<Task> action, string code, string message)
            {
                try { await action(); }
                catch (ModulePrivilegeException error) when (error.Code == "module.privilege_" + code)
                { check(true, message); return; }
                throw new Exception("Expected module application rejection: " + message);
            }
            ModuleApplicationState state = new("mysql", "mysql-admin", "relaxkonos_app", "localhost", true,
                Revision, "8.0.39-0ubuntu0.24.04.2");
            ModuleApplicationQuery? lastQuery = null;
            Task<ModuleApplicationState?> Query(ModuleApplicationQuery query)
            {
                lastQuery = query;
                return Task.FromResult<ModuleApplicationState?>(state);
            }
            ModulePrivilegeRequest Request(string key, string action, string? permission = null) => new(
                ModuleCapabilityCatalog.ApplicationManage, action, "mysql", "Rotate the fixture database credential", key,
                Secrets: [new("database-admin", "mysql", permission ?? (action == "query" ? "read" : "rotate"))]);
            Task<ModulePrivilegePrepared> Prepare(string key, string action, string? permission = null,
                Func<ModuleApplicationQuery, Task<ModuleApplicationState?>>? query = null) =>
                store.PrepareAsync("fixture", "session-1", Request(key, action, permission), "test-user",
                    queryApplication: query ?? Query);
            async Task<ModulePrivilegePlan> Read(ModulePrivilegePrepared prepared) =>
                await store.ReadForUserAsync(prepared.PlanId, "session-1");

            var queryPlan = await Read(await Prepare("app-query", "query"));
            check(lastQuery is { Instance: "mysql", Action: ModuleApplicationAction.Query }
                && queryPlan.Application is { } queried
                && queried.Instance == "mysql" && queried.Protocol == "mysql-admin" && queried.Action == "query"
                && queried.Account == "relaxkonos_app" && queried.AccountHost == "localhost"
                && queried.ServerVersion == state.ServerVersion && queried.SecretRevision == Revision
                && queryPlan.Package is null && queryPlan.File is null && queryPlan.Data is null
                && queryPlan.Effects.Contains("app-query", StringComparer.Ordinal),
                "Host freezes the instance, account, server version and opaque secret revision for a query plan");

            var queryTransport = new RecordingTransport();
            var queryApproved = await store.ApproveAsync(queryPlan.PlanId, "session-1", Approval(queryPlan), Verify,
                (operationId, execution) => CoreModulePrivilegeBroker.ExecuteCapabilityAsync(queryTransport, operationId, execution));
            var queryApplied = queryTransport.Requests.Single().ModuleApplicationApply;
            check(queryApproved.State == "Succeeded"
                && queryTransport.Requests.Single().Operation == PrivilegedOperationKind.ModuleApplicationApply
                && queryApplied is { Action: ModuleApplicationAction.Query, Instance: "mysql" }
                && queryApplied.Expected == state,
                "confirmed query plan sends only the frozen application identity and state");

            var rotatePlan = await Read(await Prepare("app-rotate", "rotate-credential"));
            check(rotatePlan.Application is { } rotating && rotating.Action == "rotate-credential"
                && rotating.SecretRevision == Revision && rotatePlan.Effects.Contains("app-rotate-credential", StringComparer.Ordinal)
                && rotatePlan.Warnings.Any(line => line.Contains("Host-generated", StringComparison.Ordinal))
                && rotatePlan.Warnings.Any(line => line.Contains("never receives", StringComparison.Ordinal))
                && rotatePlan.Warnings.Any(line => line.Contains("Lost-credential", StringComparison.Ordinal)),
                "rotation plan explains the credential change and lost-credential boundary before confirmation");

            var persistedPath = Path.Combine(root, "data", "module-management", "privilege-plans.json");
            var persisted = File.ReadAllText(persistedPath);
            var document = JsonNode.Parse(persisted)!;
            var stored = document["plans"]!.AsArray()
                .Single(node => node!["id"]!.GetValue<Guid>() == rotatePlan.PlanId)!.AsObject();
            check(document["schemaVersion"]!.GetValue<int>() == 7
                && stored["application"]!["instance"]!.GetValue<string>() == "mysql"
                && stored["application"]!["expected"]!["secretRevision"]!.GetValue<string>() == Revision
                && stored["applicationHash"]!.GetValue<string>() is { Length: 64 }
                && !persisted.Contains("password", StringComparison.OrdinalIgnoreCase)
                && !persisted.Contains("/etc/relaxkonos/secrets", StringComparison.Ordinal),
                "application plan store is schemaVersion 7 and persists no password or secret path");

            var rotateTransport = new RecordingTransport();
            var rotateApproved = await store.ApproveAsync(rotatePlan.PlanId, "session-1", Approval(rotatePlan), Verify,
                (operationId, execution) => CoreModulePrivilegeBroker.ExecuteCapabilityAsync(rotateTransport, operationId, execution));
            var rotateApplied = rotateTransport.Requests.Single().ModuleApplicationApply;
            check(rotateApproved.State == "Succeeded"
                && rotateApplied is { Action: ModuleApplicationAction.RotateCredential, Instance: "mysql" }
                && rotateApplied.Expected.SecretRevision == Revision,
                "confirmed rotation sends only the frozen instance and secret revision, never a credential");

            await Reject(() => Prepare("app-query-missing-secret", "query", permission: "rotate"),
                "target_invalid", "a query plan requires the read secret reference, not another permission");
            await Reject(() => Prepare("app-rotate-missing-secret", "rotate-credential", permission: "read"),
                "target_invalid", "a rotation plan requires the rotate secret reference");
            await Reject(() => store.PrepareAsync("fixture", "session-1",
                    new(ModuleCapabilityCatalog.ApplicationManage, "rotate-credential", "mysql", "No reference",
                        "app-no-secret"), "test-user", queryApplication: Query),
                "target_invalid", "application management requires a purpose-bound secret reference");
            await Reject(() => store.PrepareAsync("fixture", "session-1",
                    new(ModuleCapabilityCatalog.ApplicationManage, "rotate-credential", "mysql", "Undeclared purpose",
                        "app-undeclared-secret", [new("other-purpose", "mysql", "rotate")]),
                    "test-user", queryApplication: Query),
                "not_declared", "an undeclared secret purpose is never planned");
            await Reject(() => store.PrepareAsync("fixture", "session-1",
                    new(ModuleCapabilityCatalog.ApplicationManage, "configure", "mysql", "Configure",
                        "app-configure", [new("database-admin", "mysql", "write")]),
                    "test-user", queryApplication: Query),
                "unsupported", "application configure stays unsupported until a closed typed adapter exists");
            await Reject(() => store.PrepareAsync("fixture", "session-1",
                    new(ModuleCapabilityCatalog.ApplicationManage, "recover-credential", "mysql", "Recover",
                        "app-recover", [new("database-admin", "mysql", "rotate")]),
                    "test-user", queryApplication: Query),
                "unsupported", "lost-credential recovery is not claimed before service-stop and compensation adapters exist");
            await Reject(() => store.PrepareAsync("fixture", "session-1", Request("app-no-helper", "rotate-credential"),
                    "test-user", queryApplication: null),
                "unsupported", "application plan creation fails closed when the Helper cannot observe the instance");
            state = state with { AccountExists = false };
            await Reject(() => Prepare("app-account-missing", "rotate-credential"), "unsupported",
                "application management refuses an instance whose declared account does not exist");
            state = state with { AccountExists = true };

            var driftPlan = await Read(await Prepare("app-drift", "rotate-credential"));
            var driftTransport = new RecordingTransport { Result = new(false, 65, ProblemCode: PrivilegedProblemCode.PlanChanged) };
            await Reject(() => store.ApproveAsync(driftPlan.PlanId, "session-1", Approval(driftPlan), Verify,
                (operationId, execution) => CoreModulePrivilegeBroker.ExecuteCapabilityAsync(driftTransport, operationId, execution)),
                "plan_changed", "application state drift is reported as a changed plan");
            var driftStatus = await store.GetForModuleAsync(driftPlan.PlanId, "fixture");
            check(driftStatus.State == "Failed" && driftStatus.ProblemCode == "module.privilege_plan_changed",
                "application drift fails the confirmed plan instead of becoming an unknown retry");

            var recoveryPlan = await Read(await Prepare("app-recovery", "rotate-credential"));
            var recoveryTransport = new RecordingTransport { Result = new(false, 65, ProblemCode: PrivilegedProblemCode.RecoveryRequired) };
            var recovery = await store.ApproveAsync(recoveryPlan.PlanId, "session-1", Approval(recoveryPlan), Verify,
                (operationId, execution) => CoreModulePrivilegeBroker.ExecuteCapabilityAsync(recoveryTransport, operationId, execution));
            check(recovery.State == "RecoveryRequired" && recovery.ProblemCode == "module.privilege_recovery_required",
                "a rotation that may have changed the database remains unknown and is never marked success");

            var original = File.ReadAllText(persistedPath);
            void RejectCorruption(Action<JsonObject> mutate, string message)
            {
                var changed = JsonNode.Parse(original)!.AsObject();
                mutate(changed);
                File.WriteAllText(persistedPath, changed.ToJsonString());
                try
                {
                    _ = new ModulePrivilegePlanStore(root, Resolve, clock);
                    throw new Exception("Expected persisted application corruption rejection");
                }
                catch (ModulePrivilegeException error) when (error.Code == "module.privilege_recovery_required")
                { check(true, message); }
                finally { File.WriteAllText(persistedPath, original); }
            }
            RejectCorruption(changed =>
            {
                var node = changed["plans"]!.AsArray()
                    .Single(item => item!["id"]!.GetValue<Guid>() == rotatePlan.PlanId)!.AsObject();
                node["application"]!["expected"]!["secretRevision"] = NextRevision;
            }, "Broker rejects a persisted secret revision changed without the frozen hash");
            RejectCorruption(changed =>
            {
                var node = changed["plans"]!.AsArray()
                    .Single(item => item!["id"]!.GetValue<Guid>() == rotatePlan.PlanId)!.AsObject();
                node["application"] = null;
            }, "Broker rejects an application plan whose frozen identity disappeared");
            RejectCorruption(changed =>
            {
                var node = changed["plans"]!.AsArray()
                    .Single(item => item!["id"]!.GetValue<Guid>() == rotatePlan.PlanId)!.AsObject();
                node["application"]!["expected"]!["account"] = "other";
            }, "Broker rejects a persisted account changed without the frozen hash");
            trusted = false;
            await Reject(() => Prepare("app-revoked", "query"), "denied",
                "code trust revocation also blocks new application plans");
            trusted = true;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static void ValidateHelperPolicy(Action<bool, string> check)
    {
        var policy = Encoding.UTF8.GetBytes("""
            {"schemaVersion":1,"instances":[
              {"instance":"mysql","protocol":"mysql-admin","socket":"/run/mysqld/mysqld.sock",
               "adminUser":"root","adminSecret":"/etc/relaxkonos/secrets/mysql-admin.json",
               "account":"relaxkonos_app","accountHost":"localhost",
               "accountSecret":"/etc/relaxkonos/secrets/mysql-app.json",
               "actions":["query","rotate-credential"]}
            ]}
            """);
        check(modulehelper::ModuleApplicationPolicy.TryParse(policy, out var parsed, out var problem)
            && problem == "" && parsed is not null && parsed.Instances.Length == 1
            && parsed.TryFind("mysql", ModuleApplicationAction.Query, out var queryInstance) && queryInstance is not null
            && parsed.TryFind("mysql", ModuleApplicationAction.RotateCredential, out var rotateInstance)
            && rotateInstance is { AdminUser: "root", Account: "relaxkonos_app" },
            "root-owned application policy parses one mysql-admin instance and its two credential files");
        var secret = Encoding.UTF8.GetBytes(
            """{"schemaVersion":1,"revision":"11111111111111111111111111111111","user":"root","password":"fixture-password"}""");
        check(modulehelper::ModuleApplicationSecret.TryParse(secret, "root", out var parsedSecret, out var secretProblem)
            && secretProblem == "" && parsedSecret is { User: "root", Revision: "11111111111111111111111111111111" },
            "root-only application secret parses the exact revision, user and password without exposing them elsewhere");
        var invalidPolicies = new[]
        {
            """{"schemaVersion":2,"instances":[]}""",
            """{"schemaVersion":1,"instances":[{"instance":"mysql","protocol":"other","socket":"/run/mysqld/mysqld.sock","adminUser":"root","adminSecret":"/etc/relaxkonos/secrets/a.json","account":"app","accountHost":"localhost","accountSecret":"/etc/relaxkonos/secrets/b.json","actions":["query"]}]}""",
            """{"schemaVersion":1,"instances":[{"instance":"mysql","protocol":"mysql-admin","socket":"relative.sock","adminUser":"root","adminSecret":"/etc/relaxkonos/secrets/a.json","account":"app","accountHost":"localhost","accountSecret":"/etc/relaxkonos/secrets/b.json","actions":["query"]}]}""",
            """{"schemaVersion":1,"instances":[{"instance":"mysql","protocol":"mysql-admin","socket":"/run/mysqld/mysqld.sock","adminUser":"root","adminSecret":"/tmp/a.json","account":"app","accountHost":"localhost","accountSecret":"/etc/relaxkonos/secrets/b.json","actions":["query"]}]}""",
            """{"schemaVersion":1,"instances":[{"instance":"mysql","protocol":"mysql-admin","socket":"/run/mysqld/mysqld.sock","adminUser":"root","adminSecret":"/etc/relaxkonos/secrets/a.json","account":"root","accountHost":"localhost","accountSecret":"/etc/relaxkonos/secrets/b.json","actions":["query"]}]}""",
            """{"schemaVersion":1,"instances":[{"instance":"mysql","protocol":"mysql-admin","socket":"/run/mysqld/mysqld.sock","adminUser":"root","adminSecret":"/etc/relaxkonos/secrets/a.json","account":"app","accountHost":"localhost","accountSecret":"/etc/relaxkonos/secrets/b.json","actions":["configure"]}]}""",
        };
        foreach (var invalid in invalidPolicies)
            check(!modulehelper::ModuleApplicationPolicy.TryParse(Encoding.UTF8.GetBytes(invalid), out _, out _),
                "application policy rejects unknown protocol/schema, relative socket, unapproved secret path, admin-as-app and undelivered actions");
        check(!modulehelper::ModuleApplicationSecret.TryParse(secret, "other", out _, out _)
            && !modulehelper::ModuleApplicationSecret.TryParse(Encoding.UTF8.GetBytes(
                """{"schemaVersion":1,"revision":"short","user":"root","password":"x"}"""), "root", out _, out _)
            && !modulehelper::ModuleApplicationSecret.TryParse(Encoding.UTF8.GetBytes(
                """{"schemaVersion":1,"revision":"11111111111111111111111111111111","user":"root","password":"x","extra":true}"""),
                "root", out _, out _),
            "application secret rejects a mismatched user, malformed revision and unknown fields");
    }
}
