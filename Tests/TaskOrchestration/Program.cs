using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.TaskOrchestration;

var count = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine("PASS: " + label); count++;
}
JsonElement Json(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
var vector = TaskPlanCanonicalizer.Canonicalize(Json("{\"b\":[true,null,\"x\"],\"a\":1}"));
Check(Encoding.UTF8.GetString(vector) == "{\"a\":1,\"b\":[true,null,\"x\"]}", "Fixed canonical JSON vector uses ordinal properties and ordered arguments");
Check(Convert.ToHexStringLower(SHA256.HashData(vector)) == "eca8cfb31ab74533e1eb2f4c74d2d55dfe3c79ac704787e54be8647ea7777eb1",
    "Fixed canonical SHA-256 test vector is stable");
void RejectCanonical(string json, string code)
{
    try { TaskPlanCanonicalizer.Canonicalize(Json(json)); throw new Exception("Accepted malformed canonical input"); }
    catch (TaskPlanningException ex) { Check(ex.ProblemCode == code, "Canonical rejection: " + code); }
}
RejectCanonical("{\"a\":1,\"a\":2}", "task.duplicate_property");
RejectCanonical("{\"a\":1.5}", "task.input_type_invalid");
RejectCanonical("{\"a\":9223372036854775808}", "task.input_type_invalid");
var facts = new Observations();
var planner = new TaskPlanner(RuntimeReadPlanningActions.Create(facts));
var context = new TaskPlanningContext("host", "subject");
var installation = new TaskStepIntent("installation", "runtime.verifyInstallation", 1, "sing-box-read-1",
    JsonSerializer.SerializeToElement(new VerifyRuntimeInstallationInput(facts.Installation.Installation.InstallationId, 7)), []);
var catalog = new TaskStepIntent("catalog", "runtime.verifyCatalog", 1, "sing-box-read-1",
    JsonSerializer.SerializeToElement(new VerifyRuntimeCatalogInput("official", 4, 3)), ["installation", "installation"]);
var intent = new TaskPlanIntent("workload", "proxy-server", 1, [installation, catalog]);
var plan = await planner.PlanAsync(context, intent);
Check(plan.PlanDigest == "37a8af03900dd65231a18d6c8a9f8959c87ca35f1f63bb849ae39f12a9760cc2",
    "Complete two-action frozen plan has a fixed canonical digest vector");
Check(plan.Semantics.Steps.All(step => step.Disposition == TaskStepDisposition.NoOp), "Both compiled read checks resolve to no-op without submitting changes");
var parsed = TaskPlanJson.ReadIntent(TaskPlanJson.WriteIntent(intent));
Check((await planner.PlanAsync(context, parsed)).PlanDigest == plan.PlanDigest, "Strict intent wire roundtrip preserves the semantic plan");
foreach (var invalid in new[] { "{\"SourceKind\":\"app\",\"SourceKind\":\"recipe\"}", "{\"Unknown\":true}", "null" })
{
    try { TaskPlanJson.ReadIntent(Encoding.UTF8.GetBytes(invalid)); throw new Exception("Accepted invalid wire intent"); }
    catch (TaskPlanningException) { Check(true, "Strict wire reader rejects duplicate, unknown or null intent fields"); }
}
Check(plan.Semantics.Steps[0].StepId == "catalog" && plan.Semantics.Steps[0].DependsOn.SequenceEqual(["installation"]),
    "Step and set order is normalized, duplicate dependency edges are removed");
var reordered = await planner.PlanAsync(context, intent with { Steps = [catalog with { DependsOn = ["installation"] }, installation] });
Check(plan.PlanId != reordered.PlanId && plan.PlanDigest == reordered.PlanDigest,
    "Plan ID and creation time do not alter the deterministic semantic digest");
Check(TaskPlanCanonicalizer.Digest(plan.Semantics with { Steps = plan.Semantics.Steps.Reverse().ToImmutableArray() }) == plan.PlanDigest,
    "Shared canonical encoding normalizes semantic step sets independently of transport order");
Check(TaskPlanCanonicalizer.Digest(plan.Semantics with { CanonicalSubject = "another" }) != plan.PlanDigest
    && TaskPlanCanonicalizer.Digest(plan.Semantics with { HostId = "another" }) != plan.PlanDigest,
    "Host and canonical subject are bound by the digest");
Check(TaskPlanCanonicalizer.Digest(plan.Semantics with { SourceKind = "recipe" }) != plan.PlanDigest,
    "Source metadata is part of approval semantics");
Check(TaskPlanCanonicalizer.Digest(plan.Semantics with { ContinueWhenDisconnected = true }) != plan.PlanDigest,
    "Background execution consent is bound by the digest");
using (var document = JsonDocument.Parse(installation.Inputs.GetRawText()))
{
    var retained = await planner.PlanAsync(context, intent with { Steps = [installation with { Inputs = document.RootElement }] });
    document.Dispose();
    Check(TaskPlanCanonicalizer.Digest(retained.Semantics) == retained.PlanDigest,
        "Frozen plan owns its JSON inputs after the caller disposes its document");
}
async Task Reject(TaskPlanIntent invalid, string code)
{
    var reads = facts.Reads;
    try { await planner.PlanAsync(context, invalid); throw new Exception("Accepted invalid intent"); }
    catch (TaskPlanningException ex)
    { Check(ex.ProblemCode == code && facts.Reads == reads, "Rejected before domain reads: " + code); }
}
await Reject(intent with { Steps = [installation, installation] }, "task.step_id_invalid");
await Reject(intent with { Steps = [installation with { ActionId = "shell.run" }] }, "task.action_unknown");
await Reject(intent with { Steps = [installation with { ImplementationRevision = "older" }] }, "task.action_revision_unsupported");
await Reject(intent with { Steps = [installation with { SchemaVersion = 2 }] }, "task.action_revision_unsupported");
await Reject(intent with { Steps = [installation with { DependsOn = ["unknown"] }] }, "task.dependency_invalid");
await Reject(intent with { Steps = [installation with { DependsOn = [null!] }] }, "task.dependency_invalid");
await Reject(intent with { Steps = [installation with { Inputs = default }] }, "task.input_invalid");
await Reject(intent with { Steps = [installation with { DependsOn = ["catalog"] }, catalog] }, "task.dependency_cycle");
await Reject(intent with { Steps = [installation with { Inputs = Json("{\"InstallationId\":\"00000000-0000-0000-0000-000000000001\",\"ExpectedStateRevision\":7,\"Command\":\"dangerous\"}") }] }, "task.input_invalid");
await Reject(intent with { Steps = [installation with { Inputs = Json("{}") }] }, "task.input_invalid");
await Reject(intent with { Steps = [catalog with { DependsOn = [], Inputs = Json("{\"SourceId\":\"../path\",\"ExpectedCatalogRevision\":4,\"ExpectedTrustRevision\":3}") }] }, "task.input_invalid");
await Reject(intent with { Steps = [installation with { Inputs = Json("{\"ExpectedStateRevision\":7,\"ExpectedStateRevision\":8}") }] }, "task.duplicate_property");
await Reject(intent with { Steps = Enumerable.Range(0, 65).Select(i => installation with { StepId = "step" + i }).ToImmutableArray() },
    "task.intent_invalid");
await Reject(intent with { Steps = Enumerable.Range(0, 17).Select(i => installation with
    { StepId = "step" + i, DependsOn = i == 0 ? [] : ["step" + (i - 1)] }).ToImmutableArray() }, "task.dependency_depth_exceeded");
await Reject(intent with { Steps = [installation with { Inputs = JsonSerializer.SerializeToElement(new { Large = new string('x', 17000) }) }] },
    "task.input_size_exceeded");
var tinyPlanner = new TaskPlanner(RuntimeReadPlanningActions.Create(facts), TaskPlanLimits.Default with { MaximumPlanBytes = 1024 });
try { await tinyPlanner.PlanAsync(context, intent); throw new Exception("Plan quota not enforced"); }
catch (TaskPlanningException ex) { Check(ex.ProblemCode == "task.plan_size_exceeded", "Serialized semantic plan size is bounded"); }
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    var reads = facts.Reads;
    try { await planner.PlanAsync(context, intent, cancelled.Token); throw new Exception("Ignored cancellation"); }
    catch (OperationCanceledException) { Check(facts.Reads == reads, "Cancellation before planning performs no domain observation"); }
}
facts.AdvanceInstallationRevision = true;
try
{
    await planner.PlanAsync(context, intent with { Steps = [installation, installation with { StepId = "second" }] });
    throw new Exception("Conflicting baselines accepted");
}
catch (TaskPlanningException ex) { Check(ex.ProblemCode == "task.observation_changed", "Conflicting observations of one resource cannot be frozen together"); }
facts.AdvanceInstallationRevision = false;
var original = facts.Installation;
facts.Installation = original with { Installation = original.Installation with { StateRevision = 8 } };
var drift = await planner.PlanAsync(context, intent);
Check(drift.Semantics.Steps.Single(step => step.StepId == "installation") is { Disposition: TaskStepDisposition.Conflict }
    && drift.PlanDigest != plan.PlanDigest, "A changed no-op baseline becomes a conflict, never an implicit mutation");
facts.Installation = original with { PendingOperationId = Guid.NewGuid() };
Check((await planner.PlanAsync(context, intent)).Semantics.Steps.Single(step => step.StepId == "installation").Problems.Contains("runtime.operation_pending"),
    "An existing domain task remains a visible planning conflict");
facts.Installation = original;
var originalCatalog = facts.Catalog;
facts.Catalog = originalCatalog with { Fresh = false };
Check((await planner.PlanAsync(context, intent)).Semantics.Steps.Single(step => step.StepId == "catalog").Disposition == TaskStepDisposition.Conflict,
    "A stale catalog is not represented as verified");
facts.Catalog = originalCatalog with { Source = originalCatalog.Source with { Identity = originalCatalog.Source.Identity with { Trust = RuntimeTrust.Revoked } } };
Check((await planner.PlanAsync(context, intent)).Semantics.Steps.Single(step => step.StepId == "catalog").Problems.Contains("runtime.source_untrusted"),
    "Revoked source trust is a frozen conflict");
facts.Catalog = originalCatalog;
try
{
    JsonSerializer.Deserialize<TaskPlanIntent>("{\"SourceKind\":\"app\",\"SourceIdentity\":\"x\",\"InputRevision\":0,\"Steps\":[],\"Execute\":true}");
    throw new Exception("Accepted unknown wire field");
}
catch (JsonException) { Check(true, "Unknown wire execution fields are rejected"); }
Console.WriteLine($"Task orchestration: {count} checks passed. No executor or privileged action is enabled.");
Console.WriteLine($"Task planning HTTP: {await TaskPlanningHttpChecks.RunAsync(intent)} checks passed.");
Console.WriteLine($"Task plan persistence: {await TaskPlanStoreChecks.RunAsync(plan)} checks passed.");
Console.WriteLine($"Runtime change planning: {await RuntimeChangePlanningChecks.RunAsync()} checks passed.");
Console.WriteLine($"Approval/run persistence: {await TaskApprovalStoreChecks.RunAsync()} checks passed.");
Console.WriteLine($"Approval live policy: {await TaskApprovalPolicyChecks.RunAsync()} checks passed.");
Console.WriteLine($"Approval composition: {await TaskPlanApprovalChecks.RunAsync()} checks passed.");

sealed class Observations : IRuntimeTaskObservations
{
    public int Reads;
    public bool AdvanceInstallationRevision;
    private long _advance;
    public RuntimeInstallationSnapshot Installation = new(new(Guid.Parse("00000000-0000-0000-0000-000000000001"), "host", "sing-box", 7,
        RuntimeOwnership.Managed, new("linux", "x64", null, null, RuntimeInstallMode.Archive, "systemd"), "release", false),
        "1.14.3", null, null, null, null, null);
    public RuntimeCatalogSnapshot Catalog = new(new(new("official", "publisher", true, RuntimeTrust.Confirmed, 3),
        "sing-box", RuntimeSourceKind.HttpsManifest, "https://example.test/", "key", 4),
        new("official", 4, 3, DateTimeOffset.UtcNow.AddDays(1), true, []), true, null);
    public Task<RuntimeInstallationSnapshot> InstallationAsync(CancellationToken ct)
    {
        Reads++;
        return Task.FromResult(AdvanceInstallationRevision ? Installation with
        { Installation = Installation.Installation with { StateRevision = Installation.Installation.StateRevision + _advance++ } } : Installation);
    }
    public Task<RuntimeCatalogSnapshot> CatalogAsync(string sourceId, CancellationToken ct) { Reads++; return Task.FromResult(Catalog); }
}
