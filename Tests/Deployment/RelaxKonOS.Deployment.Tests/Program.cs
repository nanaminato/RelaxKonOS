using System.Collections.Immutable;
using System.Text.Json.Nodes;
using RelaxKonOS.Deployment;

DeploymentPackageKey Key(string id) => new(DeploymentPackageKind.ServerModule, id);
DeploymentRelease Release(string id, string version, params DeploymentRequirement[] requires)
    => new(Key(id), version, id + version, false, [..requires], ImmutableDictionary<string, string>.Empty);
void Check(bool ok) { if (!ok) throw new Exception("Deployment check failed."); }
var root = Release("app", "1.0.0", new DeploymentRequirement(Key("a")), new DeploymentRequirement(Key("b")))
    with { Package = new(DeploymentPackageKind.Application, "app") };
var candidates = new[] {
    Release("a", "2.0.0", new DeploymentRequirement(Key("shared"), MinimumVersion: "2.0.0")),
    Release("a", "1.0.0", new DeploymentRequirement(Key("shared"))),
    Release("b", "1.0.0", new DeploymentRequirement(Key("shared"), MaximumVersionExclusive: "2.0.0")),
    Release("shared", "1.0.0"), Release("shared", "2.0.0")
};
var resolved = DeploymentResolver.Resolve(root, candidates, []);
Check(resolved.Succeeded && resolved.OrderedReleases.Length == 4
    && resolved.OrderedReleases[0].Package == Key("shared") && resolved.OrderedReleases[1].Version == "1.0.0");
Check(DeploymentResolver.Resolve(root, [Release("a", "1.0.0", new DeploymentRequirement(root.Package)),
    Release("b", "1.0.0")], []).Problem == "deployment-dependency-cycle");
var optional = new DeploymentRequirement(Key("optional"));
var withOptional = candidates.Append(Release("optional", "1.0.0") with { Satisfied = true });
var selected = DeploymentResolver.Resolve(root, withOptional, [optional]);
Check(selected.Succeeded && selected.OrderedReleases.Single(release => release.Package == optional.Package).Satisfied);
Check(DeploymentResolver.Resolve(root, withOptional, []).OrderedReleases.All(release => release.Package != optional.Package));
var blocked = DeploymentResolver.Resolve(root, withOptional.Where(release => release.Package != Key("b")), []);
Check(!blocked.Succeeded && blocked.ProblemPackage == Key("b"));
var adminCapability = new DeploymentRequirement(Key("shared"), Capability: "admin", ContractVersion: "2");
var capable = Release("shared", "1.0.0") with { Capabilities = ImmutableDictionary<string, string>.Empty.Add("admin", "2") };
Check(DeploymentResolver.Resolve(root, [.. candidates.Where(release => release.Package != Key("shared")), capable],
    [adminCapability]).Succeeded);
var unknownContract = capable with { Capabilities = capable.Capabilities.SetItem("admin", "") };
Check(DeploymentResolver.Resolve(root, [.. candidates.Where(release => release.Package != Key("shared")), unknownContract],
    [adminCapability]).ProblemPackage == Key("shared"));
var backend = new Backend();
var journalDirectory = Path.Combine(Path.GetTempPath(), "rk-deployment-" + Guid.NewGuid().ToString("N"));
var journal = new FileDeploymentJournal(journalDirectory);
var coordinator = new UnifiedDeploymentCoordinator(backend, journal, TimeProvider.System);
try
{
    await coordinator.PlanAsync(backend.Target, root, candidates.Where(release => release.Package != Key("b")), []);
    throw new Exception("Expected unresolved dependency.");
}
catch (DeploymentResolutionException error)
{
    Check(error.Message == "deployment-dependency-conflict" && error.Package == Key("b"));
}
var plan = await coordinator.PlanAsync(backend.Target, root, candidates, []);
plan = plan with
{
    PreparedSteps = [new(Key("shared"), Guid.NewGuid(), plan.ExpiresAt,
        [new("package", ["linux"], ["shared"], true)])],
    ClientBinding = new(Guid.NewGuid(), "connection", null),
};
async Task Reject(DeploymentPlan candidate, string message)
{
    try { await coordinator.ExecuteAsync(candidate); throw new Exception("Expected rejection."); }
    catch (InvalidOperationException ex) when (ex.Message == message) { }
}
await Reject(plan with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, "deployment-plan-expired");
backend.Target = backend.Target with { ServerIdentity = "other" };
await Reject(plan, "deployment-server-changed");
backend.Target = plan.Target;
try { await coordinator.ExecuteAsync(plan); throw new Exception("Expected interruption."); }
catch (IOException) { }
await Reject(plan with { PreparedSteps = [] }, "deployment-plan-identity-changed");
var binding = plan.ClientBinding!;
var reopenedJournal = new FileDeploymentJournal(journalDirectory);
Check((await reopenedJournal.FindPendingPlanAsync(root.Package, root.Version, root.ArtifactIdentity,
    binding.AccountId, binding.ConnectionIdentity))?.Id == plan.Id);
Check(await journal.FindPendingPlanAsync(root.Package, root.Version, root.ArtifactIdentity,
    Guid.NewGuid(), binding.ConnectionIdentity) is null);
Check(await reopenedJournal.FindPendingPlanAsync(root.Package, root.Version, "different-digest",
    binding.AccountId, binding.ConnectionIdentity) is null);
Check(await reopenedJournal.FindPendingPlanAsync(root.Package, root.Version, root.ArtifactIdentity,
    binding.AccountId, "another-connection") is null);
var checkpointPath = Path.Combine(journalDirectory, plan.Id.ToString("N"));
var checkpointJson = await File.ReadAllTextAsync(checkpointPath);
var corrupt = JsonNode.Parse(checkpointJson)!.AsObject();
corrupt["Plan"]!["OrderedReleases"]![0]!["ActivationOnly"] = true;
await File.WriteAllTextAsync(checkpointPath, corrupt.ToJsonString());
Check(await reopenedJournal.FindPendingPlanAsync(root.Package, root.Version, root.ArtifactIdentity,
    binding.AccountId, binding.ConnectionIdentity) is null);
try { await reopenedJournal.FindStartedPlanAsync(plan.Id); throw new Exception("Expected corrupt plan rejection."); }
catch (InvalidDataException) { }
await File.WriteAllTextAsync(checkpointPath, checkpointJson);
var unrelatedId = Guid.NewGuid();
var unrelatedPath = Path.Combine(journalDirectory, unrelatedId.ToString("N"));
File.Move(checkpointPath, unrelatedPath);
try
{
    await reopenedJournal.FindStartedPlanAsync(unrelatedId);
    throw new Exception("Expected rejection of a valid checkpoint stored under another plan ID.");
}
catch (InvalidDataException) { }
finally { File.Move(unrelatedPath, checkpointPath); }
var persisted = await journal.FindStartedPlanAsync(plan.Id) ?? throw new Exception("Missing persisted plan.");
await coordinator.ExecuteAsync(persisted);
await coordinator.ExecuteAsync(plan);
Check(await journal.FindPendingPlanAsync(root.Package, root.Version, root.ArtifactIdentity,
    binding.AccountId, binding.ConnectionIdentity) is null);
Check(backend.Applied.SequenceEqual(new[] {0, 1, 1, 2, 3}) && backend.Verifications == 1);
Console.WriteLine("Deployment resolution and checkpoint checks passed.");

sealed class Backend : IDeploymentBackend
{
    public DeploymentTarget Target = new("server", "state", "catalog");
    public List<int> Applied = [];
    public int Verifications;
    public Task<DeploymentTarget> ObserveAsync(CancellationToken cancellationToken) => Task.FromResult(Target);
    public Task ApplyOrReconcileAsync(DeploymentPlan plan, int index, CancellationToken cancellationToken)
    {
        Applied.Add(index);
        if (Applied.Count == 2) throw new IOException("Interrupted");
        return Task.CompletedTask;
    }
    public Task VerifyAsync(DeploymentPlan plan, CancellationToken cancellationToken)
    {
        Verifications++;
        return Task.CompletedTask;
    }
}

