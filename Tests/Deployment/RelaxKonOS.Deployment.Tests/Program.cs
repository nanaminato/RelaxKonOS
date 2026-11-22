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
var cyclicOrMissing = DeploymentResolver.Resolve(root,
    [Release("a", "2.0.0", new DeploymentRequirement(root.Package)),
        Release("a", "1.0.0", new DeploymentRequirement(Key("missing"))), Release("b", "1.0.0")], []);
Check(cyclicOrMissing is { Problem: "deployment-dependency-cycle" } && cyclicOrMissing.ProblemPackage == root.Package);
var deepChain = Enumerable.Range(0, 130)
    .Select(index => Release($"chain-{index}", "1.0.0", new DeploymentRequirement(Key($"chain-{index + 1}"))));
var deepCandidate = Release("a", "2.0.0", new DeploymentRequirement(Key("chain-0")));
var limited = DeploymentResolver.Resolve(root, [deepCandidate, Release("b", "1.0.0"), .. deepChain], []);
Check(limited is { Problem: "deployment-resolution-limit" } && limited.ProblemPackage == Key("chain-127"));
Check(DeploymentResolver.Resolve(root,
    [deepCandidate, Release("a", "1.0.0"), Release("b", "1.0.0"), .. deepChain], [])
    .OrderedReleases.Any(release => release.Package == Key("a") && release.Version == "1.0.0"));
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
var expired = plan with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
backend.Offline = true;
await Reject(expired, "deployment-plan-expired");
Check(await journal.FindStartedPlanAsync(expired.Id) is null);
backend.Offline = false;
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
using var cancelAfterStep = new CancellationTokenSource();
var interruptedPlan = plan with { Id = Guid.NewGuid() };
backend.AfterStep = cancelAfterStep.Cancel;
try
{
    await coordinator.ExecuteAsync(interruptedPlan, cancelAfterStep.Token);
    throw new Exception("Expected cancellation after the first step.");
}
catch (OperationCanceledException) when (cancelAfterStep.IsCancellationRequested) { }
Check(backend.Verifications == 1);
backend.AfterStep = null;
await coordinator.ExecuteAsync(interruptedPlan);
Check(backend.Applied.Skip(5).SequenceEqual(new[] {0, 1, 2, 3}) && backend.Verifications == 2);
var expiredStepPlan = plan with { Id = Guid.NewGuid(), ExpiresAt = plan.ExpiresAt.AddMinutes(1) };
backend.ReviewRequiredAt = 1;
await Reject(expiredStepPlan, "deployment-module-plan-expired");
var attempts = backend.Applied.Count;
await using (var lease = await reopenedJournal.OpenAsync(expiredStepPlan, CancellationToken.None))
    Check(lease.Checkpoint is { CompletedSteps: 1, RequiresReview: true, Verified: false });
Check(await reopenedJournal.FindPendingPlanAsync(root.Package, root.Version, root.ArtifactIdentity,
    binding.AccountId, binding.ConnectionIdentity) is null);
Check((await reopenedJournal.ReadPendingPlansAsync(binding.AccountId, binding.ConnectionIdentity))
    .Any(pending => pending.Id == expiredStepPlan.Id));
backend.ReviewRequiredAt = null;
await Reject(expiredStepPlan, "deployment-plan-review-required");
Check(backend.Applied.Count == attempts);
var replacement = plan with { Id = Guid.NewGuid(), ExpiresAt = expiredStepPlan.ExpiresAt.AddMinutes(1) };
await coordinator.ExecuteAsync(replacement);
Check((await reopenedJournal.ReadPendingPlansAsync(binding.AccountId, binding.ConnectionIdentity)).Count == 0);
var sourceA = new DeploymentComponentRelease("source-a", "fixture", "publisher-a", "release-a");
var sourceB = sourceA with { SourceId = "source-b", PublisherId = "publisher-b" };
var downloadA = plan with { Id = Guid.NewGuid(), ClientBinding = binding with { ComponentRelease = sourceA } };
var downloadB = plan with { Id = Guid.NewGuid(), ExpiresAt = plan.ExpiresAt.AddMinutes(1),
    ClientBinding = binding with { ComponentRelease = sourceB } };
foreach (var download in new[] { downloadA, downloadB })
{
    await using var lease = await reopenedJournal.OpenAsync(download, CancellationToken.None);
    await lease.SaveAsync(new DeploymentCheckpoint(0, false), CancellationToken.None);
}
Check((await reopenedJournal.FindPendingPlanAsync(root.Package, root.Version, root.ArtifactIdentity,
    binding.AccountId, binding.ConnectionIdentity, componentRelease: sourceA))?.Id == downloadA.Id);
Check((await reopenedJournal.FindPendingPlanAsync(root.Package, root.Version, root.ArtifactIdentity,
    binding.AccountId, binding.ConnectionIdentity, componentRelease: sourceB))?.Id == downloadB.Id);
Check(await reopenedJournal.FindPendingPlanAsync(root.Package, root.Version, root.ArtifactIdentity,
    binding.AccountId, binding.ConnectionIdentity) is null);
var concurrentPlan = plan with { Id = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };
var firstStepEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var allowFirstStep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
backend.FinishStepAsync = () =>
{
    firstStepEntered.TrySetResult();
    return allowFirstStep.Task;
};
var appliedBefore = backend.Applied.Count;
var verifiedBefore = backend.Verifications;
var firstExecution = coordinator.ExecuteAsync(concurrentPlan);
var secondCoordinator = new UnifiedDeploymentCoordinator(backend, reopenedJournal, TimeProvider.System);
try
{
    await firstStepEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    try
    {
        await secondCoordinator.ExecuteAsync(concurrentPlan).WaitAsync(TimeSpan.FromSeconds(10));
        throw new Exception("Expected a concurrent execution to be rejected.");
    }
    catch (IOException error) when (error.Message == "deployment-plan-in-progress") { }
    Check(backend.Applied.Count == appliedBefore + 1 && backend.Verifications == verifiedBefore);
}
finally
{
    allowFirstStep.TrySetResult();
    await firstExecution;
    backend.FinishStepAsync = null;
}
await secondCoordinator.ExecuteAsync(concurrentPlan);
Check(backend.Applied.Skip(appliedBefore).SequenceEqual(Enumerable.Range(0, concurrentPlan.OrderedReleases.Length))
    && backend.Verifications == verifiedBefore + 1);
var reviewAtVerification = plan with { Id = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };
backend.ReviewRequiredOnVerification = true;
await Reject(reviewAtVerification, "deployment-client-dependency-changed");
await using (var lease = await reopenedJournal.OpenAsync(reviewAtVerification, CancellationToken.None))
    Check(lease.Checkpoint is { RequiresReview: true, Verified: false } reviewCheckpoint
        && reviewCheckpoint.CompletedSteps == reviewAtVerification.OrderedReleases.Length);
var appliedAfterVerificationFailure = backend.Applied.Count;
var verificationAttempts = backend.Verifications;
backend.ReviewRequiredOnVerification = false;
await Reject(reviewAtVerification, "deployment-plan-review-required");
Check(backend.Applied.Count == appliedAfterVerificationFailure && backend.Verifications == verificationAttempts);
var retryVerification = plan with { Id = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };
backend.VerificationOffline = true;
try
{
    await coordinator.ExecuteAsync(retryVerification);
    throw new Exception("Expected interrupted verification.");
}
catch (IOException error) when (error.Message == "Verification unavailable") { }
await using (var lease = await reopenedJournal.OpenAsync(retryVerification, CancellationToken.None))
    Check(lease.Checkpoint is { RequiresReview: false, Verified: false } retryCheckpoint
        && retryCheckpoint.CompletedSteps == retryVerification.OrderedReleases.Length);
var appliedBeforeRetry = backend.Applied.Count;
backend.VerificationOffline = false;
await secondCoordinator.ExecuteAsync(retryVerification);
Check(backend.Applied.Count == appliedBeforeRetry);
await using (var lease = await reopenedJournal.OpenAsync(retryVerification, CancellationToken.None))
    Check(lease.Checkpoint is { Verified: true, RequiresReview: false });
var reviewAtPreflight = plan with { Id = Guid.NewGuid() };
await using (var lease = await journal.OpenAsync(reviewAtPreflight, CancellationToken.None))
    await lease.SaveAsync(new(1, false), CancellationToken.None);
var appliedBeforePreflight = backend.Applied.Count;
backend.ReviewRequiredOnPreflight = true;
await Reject(reviewAtPreflight, "deployment-client-dependency-changed");
backend.ReviewRequiredOnPreflight = false;
await using (var lease = await reopenedJournal.OpenAsync(reviewAtPreflight, CancellationToken.None))
    Check(lease.Checkpoint is { RequiresReview: true, CompletedSteps: 1, Verified: false });
await Reject(reviewAtPreflight, "deployment-plan-review-required");
Check(backend.Applied.Count == appliedBeforePreflight);
Console.WriteLine("Deployment resolution and checkpoint checks passed.");

sealed class Backend : IDeploymentBackend
{
    public DeploymentTarget Target = new("server", "state", "catalog");
    public List<int> Applied = [];
    public int Verifications;
    public Action AfterStep;
    public Func<Task> FinishStepAsync;
    public int? ReviewRequiredAt;
    public bool Offline;
    public bool ReviewRequiredOnVerification;
    public bool VerificationOffline;
    public bool ReviewRequiredOnPreflight;
    public Task VerifyBeforeExecutionAsync(DeploymentPlan plan, CancellationToken cancellationToken)
    {
        if (ReviewRequiredOnPreflight)
            throw new DeploymentPlanReviewRequiredException("deployment-client-dependency-changed");
        return Task.CompletedTask;
    }
    public Task<DeploymentTarget> ObserveAsync(CancellationToken cancellationToken) =>
        Offline ? Task.FromException<DeploymentTarget>(new IOException("Server unreachable")) : Task.FromResult(Target);
    public Task ApplyOrReconcileAsync(DeploymentPlan plan, int index, CancellationToken cancellationToken)
    {
        Applied.Add(index);
        if (Applied.Count == 2) throw new IOException("Interrupted");
        if (ReviewRequiredAt == index) throw new DeploymentPlanReviewRequiredException("deployment-module-plan-expired");
        AfterStep?.Invoke();
        return FinishStepAsync?.Invoke() ?? Task.CompletedTask;
    }
    public Task VerifyAsync(DeploymentPlan plan, CancellationToken cancellationToken)
    {
        Verifications++;
        if (ReviewRequiredOnVerification)
            throw new DeploymentPlanReviewRequiredException("deployment-client-dependency-changed");
        if (VerificationOffline) throw new IOException("Verification unavailable");
        return Task.CompletedTask;
    }
}

