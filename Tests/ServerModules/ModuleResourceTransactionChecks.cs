extern alias modulehelper;

using System.Text;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Startup;
using System.Text.Json.Nodes;

internal static class ModuleResourceTransactionChecks
{
    private const string FileRoot = "/var/tmp/relaxkonos-dc03/etc/mysql";
    private const string FilePath = FileRoot + "/mysql.cnf";
    private const string DataRoot = "/var/tmp/relaxkonos-dc03/data/mysql";
    private const string Content = "innodb_buffer_pool_size=1G\n";
    private const string Existing = "existing=1\n";
    private const string ExistingSha = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string DataSha = "2222222222222222222222222222222222222222222222222222222222222222";

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Current = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Current;
    }

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
        ValidatePolicy(check);
        var root = Path.Combine(Path.GetTempPath(), "module-resources-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock();
            var identity = new ModulePrivilegeIdentity("fixture", "1.0.0", "publisher", new string('a', 64), new string('b', 64),
                "revision-1",
                [
                    new FileManageCapability
                    {
                        Platforms = ["linux"], Root = FileRoot, Actions = ["write", "delete"], MaximumBytes = 4096,
                    },
                    new DataManageCapability
                    {
                        Platforms = ["linux"], Root = DataRoot, Actions = ["preserve", "backup", "delete"],
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
                throw new Exception("Expected module resource rejection: " + message);
            }
            var fileState = new ModuleFileState(false, false, false, "", 0, 0, 0, 420);
            ModuleFileQuery? lastFileQuery = null;
            Task<ModuleFileState?> QueryFile(ModuleFileQuery query)
            {
                lastFileQuery = query;
                return Task.FromResult<ModuleFileState?>(fileState);
            }
            ModuleDataState dataState = new(true, true, 2, 4096, DataSha);
            ModuleDataQuery? lastDataQuery = null;
            Task<ModuleDataState?> QueryData(ModuleDataQuery query)
            {
                lastDataQuery = query;
                return Task.FromResult<ModuleDataState?>(dataState);
            }
            Task<ModulePrivilegePrepared> PrepareFile(string key, string action, string? content,
                Func<ModuleFileQuery, Task<ModuleFileState?>>? query = null) =>
                store.PrepareAsync("fixture", "session-1",
                    new(ModuleCapabilityCatalog.FileManage, action, FilePath, "Publish the fixture configuration", key,
                        ContentBase64: content), "test-user", null, query ?? QueryFile, null);
            Task<ModulePrivilegePrepared> PrepareData(string key, string action, string? backupRef) =>
                store.PrepareAsync("fixture", "session-1",
                    new(ModuleCapabilityCatalog.DataManage, action, DataRoot, "Manage the fixture data root", key,
                        BackupRef: backupRef), "test-user", null, null, QueryData);
            async Task<ModulePrivilegePlan> Read(ModulePrivilegePrepared prepared) =>
                await store.ReadForUserAsync(prepared.PlanId, "session-1");

            var write = await Read(await PrepareFile("file-write", "write", Convert.ToBase64String(Encoding.UTF8.GetBytes(Content))));
            check(lastFileQuery is { Root: FileRoot, Path: FilePath, Action: ModuleFileAction.Write }
                && write.File is { } written
                && written.Root == FileRoot && written.Path == FilePath && written.Action == "write"
                && written.BeforeKind == "absent" && written.BeforeSha256 == "" && written.BeforeBytes == 0
                && written.AfterKind == "file" && written.AfterBytes == Encoding.UTF8.GetByteCount(Content)
                && written.AfterSha256 == Sha(Content) && written.Mode == 420
                && write.Package is null && write.Data is null
                && write.Effects.Contains("file-write", StringComparer.Ordinal)
                && write.Warnings.Any(line => line.Contains("secret reference", StringComparison.Ordinal)),
                "Host freezes the canonical path, observed state, exact content digest and mode into a file plan");

            var persistedPath = Path.Combine(root, "data", "module-management", "privilege-plans.json");
            var persisted = File.ReadAllText(persistedPath);
            var document = JsonNode.Parse(persisted)!;
            var stored = document["plans"]!.AsArray()
                .Single(node => node!["id"]!.GetValue<Guid>() == write.PlanId)!.AsObject();
            check(document["schemaVersion"]!.GetValue<int>() == 7
                && stored["file"]!["path"]!.GetValue<string>() == FilePath
                && stored["fileHash"]!.GetValue<string>() is { Length: 64 }
                && stored["request"]!["contentBase64"] is null
                && !persisted.Contains(Content, StringComparison.Ordinal)
                && !persisted.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(Content)), StringComparison.Ordinal),
                "file plan store is schemaVersion 7 and persists only the digest, never the configuration body");

            byte[]? dispatched = null;
            var transport = new RecordingTransport();
            var approved = await store.ApproveAsync(write.PlanId, "session-1", Approval(write), Verify,
                async (operationId, execution) =>
                {
                    dispatched = execution.Content;
                    check(execution.File is not null && execution.Data is null && execution.Package is null
                        && execution.Request.Target == FilePath,
                        "confirmed file plan dispatches the frozen resource identity only");
                    return await CoreModulePrivilegeBroker.ExecuteCapabilityAsync(transport, operationId, execution);
                });
            var applied = transport.Requests.Single().ModuleFileApply;
            check(approved.State == "Succeeded" && transport.Requests.Single().Operation == PrivilegedOperationKind.ModuleFileApply
                && applied is not null && applied.Action == ModuleFileAction.Write && applied.Root == FileRoot
                && applied.Path == FilePath && applied.Mode == 420
                && applied.Expected == fileState && applied.ContentBytes == Encoding.UTF8.GetByteCount(Content)
                && applied.ContentSha256 == Sha(Content)
                && dispatched is not null && Encoding.UTF8.GetString(dispatched) == Content,
                "confirmed file plan sends only the frozen path, observation, digest and confirmed bytes as ModuleFileApply");

            // A pending file publication cannot survive a restart without its content. It must expire
            // rather than execute bytes that differ from the digest the user confirmed.
            var pendingWrite = await PrepareFile("file-pending", "write", Convert.ToBase64String(Encoding.UTF8.GetBytes(Content)));
            var pendingPlan = await Read(pendingWrite);
            var reopened = new ModulePrivilegePlanStore(root, Resolve, clock);
            check((await reopened.GetForModuleAsync(pendingPlan.PlanId, "fixture")).State == "Expired"
                && (await reopened.ReadForUserAsync(pendingPlan.PlanId, "session-1")).ConfirmationToken == "",
                "a pending file publication expires on Core restart instead of executing unconfirmed bytes");
            await Reject(() => reopened.ApproveAsync(pendingPlan.PlanId, "session-1", Approval(pendingPlan), Verify,
                (_, _) => Task.FromResult(true)), "plan_expired", "an expired file publication cannot be approved later");

            fileState = new ModuleFileState(true, false, false, ExistingSha, Existing.Length, 0, 0, 416);
            var delete = await Read(await PrepareFile("file-delete", "delete", null));
            check(delete.File is { } deleted && deleted.BeforeKind == "file" && deleted.BeforeSha256 == ExistingSha
                && deleted.BeforeBytes == Existing.Length && deleted.AfterKind == "absent"
                && deleted.AfterSha256 == "" && deleted.AfterBytes == 0 && deleted.Mode == 416,
                "Host freezes the existing digest and exact absence target for a file delete");
            var deleteTransport = new RecordingTransport();
            var deleteApproved = await store.ApproveAsync(delete.PlanId, "session-1", Approval(delete), Verify,
                (operationId, execution) => CoreModulePrivilegeBroker.ExecuteCapabilityAsync(deleteTransport, operationId, execution));
            var deleteApplied = deleteTransport.Requests.Single().ModuleFileApply;
            check(deleteApproved.State == "Succeeded" && deleteApplied is not null
                && deleteApplied.Action == ModuleFileAction.Delete && deleteApplied.ContentBase64 is null
                && deleteApplied.ContentSha256 == "" && deleteApplied.Expected.Sha256 == ExistingSha,
                "confirmed file delete sends the frozen before state and no content");

            fileState = new ModuleFileState(false, false, false, "", 0, 0, 0, 420);
            await Reject(() => PrepareFile("file-no-content", "write", null), "target_invalid",
                "file write requires the exact bytes the user will confirm");
            await Reject(() => PrepareFile("file-content-on-delete", "delete", Convert.ToBase64String([1])), "target_invalid",
                "file delete refuses content fields");
            await Reject(() => store.PrepareAsync("fixture", "session-1",
                    new(ModuleCapabilityCatalog.FileManage, "write", "/var/tmp/relaxkonos-dc03/other/outside.cnf",
                        "Publish outside the declared root", "file-outside-root", ContentBase64: Convert.ToBase64String([1])),
                    "test-user", null, QueryFile, null),
                "not_declared", "file target outside the declared root is never planned");
            await Reject(() => store.PrepareAsync("fixture", "session-1",
                    new(ModuleCapabilityCatalog.FileManage, "set-mode", FilePath, "Change mode", "file-mode"),
                    "test-user", null, QueryFile, null),
                "unsupported", "file set-mode stays unsupported until its own adapter exists");
            await Reject(() => PrepareFile("file-too-large", "write", Convert.ToBase64String(new byte[4097])), "target_invalid",
                "file content larger than the declared maximum is refused before a plan exists");
            fileState = new ModuleFileState(true, true, false, "", 0, 0, 0, 420);
            await Reject(() => PrepareFile("file-directory", "write", Convert.ToBase64String([1])), "target_invalid",
                "a file publication never replaces a directory");
            fileState = new ModuleFileState(false, false, false, "", 0, 0, 0, 420);
            await Reject(() => store.PrepareAsync("fixture", "session-1",
                    new(ModuleCapabilityCatalog.FileManage, "write", FilePath, "Publish", "file-no-helper",
                        ContentBase64: Convert.ToBase64String([1])), "test-user", null, null, null),
                "unsupported", "file plan creation fails closed when the Helper cannot observe the path");

            var driftWrite = await PrepareFile("file-drift", "write", Convert.ToBase64String(Encoding.UTF8.GetBytes(Content)));
            var driftPlan = await Read(driftWrite);
            var driftTransport = new RecordingTransport { Result = new(false, 65, ProblemCode: PrivilegedProblemCode.PlanChanged) };
            await Reject(() => store.ApproveAsync(driftPlan.PlanId, "session-1", Approval(driftPlan), Verify,
                (operationId, execution) => CoreModulePrivilegeBroker.ExecuteCapabilityAsync(driftTransport, operationId, execution)),
                "plan_changed", "file observation drift is reported as a changed plan");
            var driftStatus = await store.GetForModuleAsync(driftPlan.PlanId, "fixture");
            check(driftStatus.State == "Failed" && driftStatus.ProblemCode == "module.privilege_plan_changed",
                "file drift fails the confirmed plan instead of becoming an unknown retry");

            var preserve = await Read(await PrepareData("data-preserve", "preserve", null));
            check(lastDataQuery is { Root: DataRoot, Action: ModuleDataAction.Preserve }
                && preserve.Data is { } preserved && preserved.Root == DataRoot && preserved.Action == "preserve"
                && preserved.BeforeKind == "directory" && preserved.BeforeFiles == 2
                && preserved.BeforeBytes == 4096 && preserved.BeforeDigest == DataSha && preserved.BackupRef == ""
                && preserve.File is null && preserve.Package is null && preserve.Effects.Contains("data-preserve", StringComparer.Ordinal),
                "Host freezes the exact structural digest of a declared data root");
            var preserveTransport = new RecordingTransport();
            var preserveApproved = await store.ApproveAsync(preserve.PlanId, "session-1", Approval(preserve), Verify,
                (operationId, execution) => CoreModulePrivilegeBroker.ExecuteCapabilityAsync(preserveTransport, operationId, execution));
            var preserveApplied = preserveTransport.Requests.Single().ModuleDataApply;
            check(preserveApproved.State == "Succeeded" && preserveApplied is not null
                && preserveApplied.Action == ModuleDataAction.Preserve && preserveApplied.Root == DataRoot
                && preserveApplied.Scope == "fixture" && preserveApplied.BackupRef is null
                && preserveApplied.Expected.Digest == DataSha,
                "confirmed data preserve sends only the frozen root digest and authenticated module scope");

            var backup = await Read(await PrepareData("data-backup", "backup", "pre-upgrade"));
            check(backup.Data is { } backedUp && backedUp.Action == "backup" && backedUp.BackupRef == "pre-upgrade"
                && backup.Warnings.Any(line => line.Contains("Host backup root", StringComparison.Ordinal)),
                "data backup freezes the module-scoped backup reference and explains where the copy is written");
            var backupTransport = new RecordingTransport();
            await store.ApproveAsync(backup.PlanId, "session-1", Approval(backup), Verify,
                (operationId, execution) => CoreModulePrivilegeBroker.ExecuteCapabilityAsync(backupTransport, operationId, execution));
            check(backupTransport.Requests.Single().ModuleDataApply is { Action: ModuleDataAction.Backup, BackupRef: "pre-upgrade" },
                "confirmed data backup sends the frozen backup reference");
            await Reject(() => PrepareData("data-backup-missing-ref", "backup", null), "target_invalid",
                "data backup requires an explicit module-scoped backup reference");
            await Reject(() => PrepareData("data-preserve-with-ref", "preserve", "other"), "target_invalid",
                "only data backup may carry a backup reference");

            var deleteData = await Read(await PrepareData("data-delete", "delete", null));
            check(deleteData.Data is { } deleting && deleting.Action == "delete"
                && deleteData.Warnings.Any(line => line.Contains("irreversible", StringComparison.Ordinal))
                && deleteData.Warnings.Any(line => line.Contains("before deleting data", StringComparison.Ordinal))
                && deleteData.Effects.Contains("data-delete", StringComparer.Ordinal),
                "data delete plan explains the irreversible loss before confirmation");
            var dataDeleteTransport = new RecordingTransport();
            await store.ApproveAsync(deleteData.PlanId, "session-1", Approval(deleteData), Verify,
                (operationId, execution) => CoreModulePrivilegeBroker.ExecuteCapabilityAsync(dataDeleteTransport, operationId, execution));
            check(dataDeleteTransport.Requests.Single().ModuleDataApply is { Action: ModuleDataAction.Delete },
                "confirmed data delete sends only the frozen root and digest");
            await Reject(() => store.PrepareAsync("fixture", "session-1",
                    new(ModuleCapabilityCatalog.DataManage, "restore", DataRoot, "Restore data", "data-restore"),
                    "test-user", null, null, QueryData),
                "unsupported", "data restore stays unsupported until its recovery design is delivered");
            await Reject(() => store.PrepareAsync("fixture", "session-1",
                    new(ModuleCapabilityCatalog.DataManage, "preserve", DataRoot + "/mysql", "Preserve a child", "data-child"),
                    "test-user", null, null, QueryData),
                "not_declared", "data lifecycle names the exact declared root, never an arbitrary descendant");
            dataState = new ModuleDataState(false, false, 0, 0, "");
            await Reject(() => PrepareData("data-absent", "preserve", null), "unsupported",
                "data lifecycle refuses an absent root instead of creating or inventing one");
            dataState = new ModuleDataState(true, true, 2, 4096, DataSha);

            var dataDrift = await Read(await PrepareData("data-drift", "delete", null));
            var dataDriftTransport = new RecordingTransport { Result = new(false, 65, ProblemCode: PrivilegedProblemCode.PlanChanged) };
            await Reject(() => store.ApproveAsync(dataDrift.PlanId, "session-1", Approval(dataDrift), Verify,
                (operationId, execution) => CoreModulePrivilegeBroker.ExecuteCapabilityAsync(dataDriftTransport, operationId, execution)),
                "plan_changed", "data digest drift is reported as a changed plan");

            var original = File.ReadAllText(persistedPath);
            void RejectCorruption(Action<JsonObject> mutate, string message)
            {
                var changed = JsonNode.Parse(original)!.AsObject();
                mutate(changed);
                File.WriteAllText(persistedPath, changed.ToJsonString());
                try
                {
                    _ = new ModulePrivilegePlanStore(root, Resolve, clock);
                    throw new Exception("Expected persisted resource corruption rejection");
                }
                catch (ModulePrivilegeException error) when (error.Code == "module.privilege_recovery_required")
                { check(true, message); }
                finally { File.WriteAllText(persistedPath, original); }
            }
            JsonObject StoredPlan(Guid id) => JsonNode.Parse(original)!["plans"]!.AsArray()
                .Single(node => node!["id"]!.GetValue<Guid>() == id)!.AsObject();
            RejectCorruption(document =>
            {
                var node = document["plans"]!.AsArray()
                    .Single(item => item!["id"]!.GetValue<Guid>() == write.PlanId)!.AsObject();
                node["file"]!["path"] = FileRoot + "/other.cnf";
            }, "Broker rejects a persisted file path changed without the frozen hash");
            RejectCorruption(document =>
            {
                var node = document["plans"]!.AsArray()
                    .Single(item => item!["id"]!.GetValue<Guid>() == write.PlanId)!.AsObject();
                node["file"]!["contentSha256"] = new string('0', 64);
            }, "Broker rejects persisted file content changed without the plan hash");
            RejectCorruption(document =>
            {
                var node = document["plans"]!.AsArray()
                    .Single(item => item!["id"]!.GetValue<Guid>() == write.PlanId)!.AsObject();
                node["file"] = null;
            }, "Broker rejects a file plan whose frozen identity disappeared");
            RejectCorruption(document =>
            {
                var node = document["plans"]!.AsArray()
                    .Single(item => item!["id"]!.GetValue<Guid>() == preserve.PlanId)!.AsObject();
                node["data"]!["expected"]!["digest"] = new string('3', 64);
            }, "Broker rejects a persisted data digest changed without the frozen hash");
            RejectCorruption(document =>
            {
                var node = document["plans"]!.AsArray()
                    .Single(item => item!["id"]!.GetValue<Guid>() == preserve.PlanId)!.AsObject();
                node["file"] = JsonNode.Parse(StoredPlan(write.PlanId)["file"]!.ToJsonString());
                node["fileHash"] = StoredPlan(write.PlanId)["fileHash"]!.GetValue<string>();
            }, "Broker rejects a file identity attached to a data plan");
            _ = StoredPlan(write.PlanId);
            trusted = false;
            await Reject(() => store.PrepareAsync("fixture", "session-1",
                    new(ModuleCapabilityCatalog.FileManage, "delete", FilePath, "Delete", "file-revoked"),
                    "test-user", null, QueryFile, null),
                "denied", "code trust revocation also blocks new file and data plans");
            trusted = true;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void ValidatePolicy(Action<bool, string> check)
    {
        var valid = Encoding.UTF8.GetBytes("""
            {"schemaVersion":1,"roots":[
              {"kind":"file","root":"/etc/mysql/mysql.conf.d","actions":["write","delete"],"maximumBytes":1048576,"mode":420,"owner":0,"group":0},
              {"kind":"data","root":"/var/lib/mysql","actions":["preserve","backup","delete"],"maximumBytes":1073741824}
            ]}
            """);
        check(modulehelper::ModuleResourcePolicy.TryParse(valid, out var policy, out var problem)
            && problem == "" && policy is not null && policy.Roots.Length == 2
            && policy.TryFindFile("/etc/mysql/mysql.conf.d", "write", out var fileRoot) && fileRoot is { Mode: 420 }
            && policy.TryFindData("/var/lib/mysql", "backup", out var dataRoot) && dataRoot is { MaximumBytes: 1073741824 }
            && !policy.TryFindFile("/etc/mysql/mysql.conf.d", "set-mode", out _),
            "root-owned module resource policy parses the documented camelCase roots and their closed actions");
        check(!modulehelper::ModuleResourcePolicy.TryParse(Encoding.UTF8.GetBytes(
                """{"schemaVersion":2,"roots":[{"kind":"file","root":"/etc/x","actions":["write"],"maximumBytes":1}]}"""),
                out _, out _)
            && !modulehelper::ModuleResourcePolicy.TryParse(Encoding.UTF8.GetBytes(
                """{"schemaVersion":1,"roots":[{"kind":"file","root":"/etc/x","actions":["write"],"maximumBytes":1,"extra":true}]}"""),
                out _, out _)
            && !modulehelper::ModuleResourcePolicy.TryParse(Encoding.UTF8.GetBytes(
                """{"SchemaVersion":1,"Roots":[{"Kind":"file","Root":"/etc/x","Actions":["write"],"MaximumBytes":1}]}"""),
                out _, out _),
            "module resource policy rejects a changed schema, unknown members and non-camelCase fields");
        var invalidPolicies = new[]
        {
            """{"schemaVersion":1,"roots":[{"kind":"file","root":"/etc/../x","actions":["write"],"maximumBytes":1}]}""",
            """{"schemaVersion":1,"roots":[{"kind":"file","root":"etc/x","actions":["write"],"maximumBytes":1}]}""",
            """{"schemaVersion":1,"roots":[{"kind":"file","root":"/etc/x","actions":["set-owner"],"maximumBytes":1}]}""",
            """{"schemaVersion":1,"roots":[{"kind":"data","root":"/var/lib/x","actions":["restore"],"maximumBytes":1}]}""",
            """{"schemaVersion":1,"roots":[{"kind":"data","root":"/var/lib/x","actions":["delete"],"maximumBytes":0}]}""",
            """{"schemaVersion":1,"roots":[{"kind":"file","root":"/etc/x","actions":["write"],"maximumBytes":1,"mode":4095}]}""",
            """{"schemaVersion":1,"roots":[{"kind":"file","root":"/etc/x","actions":["write"],"maximumBytes":1},{"kind":"file","root":"/etc/x","actions":["delete"],"maximumBytes":1}]}""",
        };
        for (var index = 0; index < invalidPolicies.Length; index++)
            check(!modulehelper::ModuleResourcePolicy.TryParse(Encoding.UTF8.GetBytes(invalidPolicies[index]), out _, out _),
                "module resource policy rejects traversal, relative roots, undelivered actions, zero limits, special mode bits and duplicates: " + index);
    }

    private static string Sha(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
