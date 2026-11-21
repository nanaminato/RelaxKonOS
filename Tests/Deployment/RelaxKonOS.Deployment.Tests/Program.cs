using System.Collections.Immutable;
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
Check(!DeploymentResolver.Resolve(root, withOptional.Where(release => release.Package != Key("b")), []).Succeeded);
var backend = new Backend();
var journal = new FileDeploymentJournal(Path.Combine(Path.GetTempPath(), "rk-deployment-" + Guid.NewGuid().ToString("N")));
var coordinator = new UnifiedDeploymentCoordinator(backend, journal, TimeProvider.System);
var plan = await coordinator.PlanAsync(backend.Target, root, candidates, []);
plan = plan with
{
    PreparedSteps = [new(Key("shared"), Guid.NewGuid(), plan.ExpiresAt,
        [new("package", ["linux"], ["shared"], true)])]
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
var persisted = await journal.FindStartedPlanAsync(plan.Id) ?? throw new Exception("Missing persisted plan.");
await coordinator.ExecuteAsync(persisted);
await coordinator.ExecuteAsync(plan);
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

