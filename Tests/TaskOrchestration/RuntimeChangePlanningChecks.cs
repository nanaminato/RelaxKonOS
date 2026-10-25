using System.Collections.Immutable;
using System.Text.Json;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.TaskOrchestration;

internal static class RuntimeChangePlanningChecks
{
    public static TaskPlanIntent Intent(RuntimeChange change, RuntimeTaskChangeInput? input = null)
    {
        input ??= Input(change);
        return new("app", "relaxkonos.component-center", 1,
            [new("runtime", RuntimeChangePlanningActions.ActionId(change), 1, RuntimeChangePlanningActions.ImplementationRevision,
                JsonSerializer.SerializeToElement(input), [])]);
    }
    public static RuntimeTaskChangeInput Input(RuntimeChange change) => new(RuntimeChangeFixture.InstallationId, "host", 7,
        change == RuntimeChange.Uninstall ? 0 : 4, change == RuntimeChange.Uninstall ? 0 : 3,
        change == RuntimeChange.Uninstall ? null : "official", change == RuntimeChange.Uninstall ? null : "target", false, true);

    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool condition, string label)
        { if (!condition) throw new Exception(label); count++; Console.WriteLine("PASS: Change plan " + label); }
        var domain = new RuntimeChangeFixture();
        var planner = new TaskPlanner(RuntimeChangePlanningActions.Create(domain));
        var context = new TaskPlanningContext("host", "subject");
        var intent = Intent(RuntimeChange.Update);
        var plan = await planner.PlanAsync(context, intent);
        var step = plan.Semantics.Steps.Single();
        var frozen = TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(step.Inputs);
        Check(step.Disposition == TaskStepDisposition.Ready && frozen is { Change: RuntimeChange.Update, Release.ReleaseId: "target" }
            && frozen.Release.Artifact.Sha256 == domain.Release.Artifact.Sha256 && frozen.PreviousReleaseId == "current",
            "freezes the exact domain-approved release and full artifact identity");
        Check(step.RequiredCapabilities is [{ Capability: "SingBoxInstall", Target: "singbox" }]
            && step.ResourceKeys.SequenceEqual(SingBoxInstallationService.ResourceKeys.Order(StringComparer.Ordinal)),
            "uses the same capability, target and resource identities as the installation ledger");
        Check(!step.Inputs.GetRawText().Contains(RuntimeChangePlanningActions.PlanningProbeKey, StringComparison.Ordinal)
            && !step.Inputs.GetRawText().Contains("IdempotencyKey", StringComparison.OrdinalIgnoreCase)
            && domain.LastRequest?.IdempotencyKey == RuntimeChangePlanningActions.PlanningProbeKey,
            "planning probes cannot freeze or supply a reusable execution key");
        Check(frozen.Impact is { MayRestartService: true, PreservesBusinessData: true, CrossStepCompensationSupported: false },
            "restart, data preservation and cross-step compensation limitations are explicit frozen effects");
        Check((await planner.PlanAsync(context, intent)).PlanDigest == plan.PlanDigest,
            "the same change selection and host facts have deterministic semantics");
        async Task Reject(TaskPlanIntent invalid, string code)
        {
            var reads = domain.Reads; var requests = domain.Plans;
            try { await planner.PlanAsync(context, invalid); throw new Exception("Invalid change accepted"); }
            catch (TaskPlanningException ex) { Check(ex.ProblemCode == code && domain.Reads == reads && domain.Plans == requests,
                "rejects before domain access: " + code); }
        }
        await Reject(intent with { Steps = [intent.Steps[0], intent.Steps[0] with { StepId = "second", DependsOn = ["runtime"] }] },
            "task.resource_conflict");
        await Reject(Intent(RuntimeChange.Update, Input(RuntimeChange.Update) with { SourceId = "../source" }), "task.input_invalid");
        await Reject(Intent(RuntimeChange.Uninstall, Input(RuntimeChange.Uninstall) with { ReleaseId = "hidden" }), "task.input_invalid");
        await Reject(Intent(RuntimeChange.Update, Input(RuntimeChange.Update) with { KeepData = false }), "task.input_invalid");
        var modified = JsonSerializer.SerializeToNode(Input(RuntimeChange.Update))!.AsObject();
        modified["command"] = "shell";
        await Reject(intent with { Steps = [intent.Steps[0] with { Inputs = JsonSerializer.SerializeToElement(modified) }] }, "task.input_invalid");
        var previous = domain.Installation;
        foreach (var state in new[]
        {
            previous with { PendingOperationId = Guid.NewGuid() },
            previous with { Installation = previous.Installation with { RecoveryRequired = true } },
            previous with { Installation = previous.Installation with { Ownership = RuntimeOwnership.External } },
            previous with { Installation = previous.Installation with { StateRevision = 8 } }
        })
        {
            domain.Installation = state; var requests = domain.Plans;
            Check((await planner.PlanAsync(context, intent)).Semantics.Steps.Single().Disposition == TaskStepDisposition.Conflict
                && domain.Plans == requests, "pending, recovery, external ownership or changed state blocks domain planning");
        }
        domain.Installation = previous;
        domain.Refuse = true;
        var blocked = (await planner.PlanAsync(context, intent)).Semantics.Steps.Single();
        Check(blocked.Problems.Contains("runtime.source_untrusted") && blocked.Disposition == TaskStepDisposition.Conflict
            && blocked.Baselines.Length == 1, "a refused domain plan preserves its reason without asserting unobserved catalog revisions");
        domain.Refuse = false;
        domain.TamperHost = true;
        try { await planner.PlanAsync(context, intent); throw new Exception("Accepted mismatched domain plan"); }
        catch (TaskPlanningException ex) { Check(ex.ProblemCode == "task.domain_plan_mismatch", "a domain response cannot retarget the host"); }
        domain.TamperHost = false;
        var release = domain.Release;
        domain.OmitDependencies = true;
        try { await planner.PlanAsync(context, intent); throw new Exception("Accepted missing host dependencies"); }
        catch (TaskPlanningException ex) { Check(ex.ProblemCode == "task.domain_plan_mismatch", "an allowed proposal without measured host requirements is rejected"); }
        domain.OmitDependencies = false;
        domain.Release = release with { Trust = RuntimeTrust.Revoked };
        try { await planner.PlanAsync(context, intent); throw new Exception("Accepted revoked release"); }
        catch (TaskPlanningException ex) { Check(ex.ProblemCode == "task.domain_plan_mismatch", "an inconsistent domain proposal cannot freeze a revoked artifact"); }
        domain.Release = release with { Artifact = release.Artifact with { Sha256 = new string('c', 64) } };
        Check((await planner.PlanAsync(context, intent)).PlanDigest != plan.PlanDigest
            && TaskPlanCanonicalizer.Digest(plan.Semantics) == plan.PlanDigest,
            "a changed artifact changes the digest while an already frozen plan remains unchanged");
        domain.Release = release with { SortVersion = "1.13.20" };
        Check((await planner.PlanAsync(context, intent)).Semantics.Steps.Single().Disposition == TaskStepDisposition.Conflict,
            "an older target cannot be described as an update");
        domain.Release = release;
        var removal = (await planner.PlanAsync(context, Intent(RuntimeChange.Uninstall,
            Input(RuntimeChange.Uninstall) with { KeepData = false }))).Semantics.Steps.Single();
        Check(removal is { RiskClass: "destructive", Disposition: TaskStepDisposition.Ready }
            && TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(removal.Inputs) is { Release: null, Impact.DeletesBusinessData: true },
            "data-deleting removal is separately frozen as destructive without any source or release");
        domain.Installation = previous with { InstalledReleases = [] };
        var rollbackCalls = domain.Plans;
        Check((await planner.PlanAsync(context, Intent(RuntimeChange.Rollback))).Semantics.Steps.Single().Problems.Contains("runtime.rollback_unavailable")
            && domain.Plans == rollbackCalls, "rollback requires a host-reported retained release");
        domain.Installation = previous with { InstalledReleases = [new(Guid.NewGuid(), "target", "1.13.20")] };
        domain.Release = release with { SortVersion = "1.13.20", OriginalVersion = "1.13.20" };
        Check((await planner.PlanAsync(context, Intent(RuntimeChange.Rollback))).Semantics.Steps.Single().Disposition == TaskStepDisposition.Ready,
            "a domain-approved retained older release is a rollback proposal");
        domain.Release = release with { ReleaseId = "current", OriginalVersion = "1.13.21", SortVersion = "1.13.21" };
        Check((await planner.PlanAsync(context, Intent(RuntimeChange.Repair, Input(RuntimeChange.Repair) with { ReleaseId = "current" })))
            .Semantics.Steps.Single().ActionId == "runtime.repair",
            "same-release repair remains a distinct operation");
        domain.Installation = previous with { Installation = previous.Installation with { Ownership = RuntimeOwnership.Absent, ActiveReleaseId = null },
            MeasuredVersion = null };
        domain.Release = release;
        Check((await planner.PlanAsync(context, Intent(RuntimeChange.Install))).Semantics.Steps.Single().Disposition == TaskStepDisposition.Ready,
            "first installation freezes a domain-approved explicit target");
        return count;
    }
}

internal sealed class RuntimeChangeFixture : IRuntimeTaskChangePlanner
{
    public static readonly Guid InstallationId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public RuntimeInstallationSnapshot Installation = new(new(InstallationId, "host", "sing-box", 7, RuntimeOwnership.Managed,
        new("linux", "x64", null, null, RuntimeInstallMode.Archive, "systemd"), "current", false), "1.13.21", null, null, null, null, null);
    public RuntimeRelease Release = new("target", "sing-box", "1.14.3", "1.14.3", RuntimeChannel.Stable, "official",
        new("linux", "x64", null, null, RuntimeInstallMode.Archive, "systemd"),
        new("artifact", new string('a', 64), 1, "zip", "sing-box", "signed-key",
            new(1, 1, [new("sing-box", 1, new string('b', 64), true)])), "sing-box-v1", true, RuntimeTrust.Confirmed, RuntimeEvidence.NativeConfiguration, null);
    public int Reads, Plans;
    public bool Refuse, TamperHost, OmitDependencies;
    public RuntimePlanRequest? LastRequest;
    public Func<RuntimeDependencyObservation, RuntimeDependencyObservation>? TransformDependency;
    public Task<RuntimeInstallationSnapshot> InstallationAsync(CancellationToken ct) { Reads++; return Task.FromResult(Installation); }
    public Task<RuntimePlanResult> PlanAsync(string? sourceId, RuntimePlanRequest request, CancellationToken ct)
    {
        Plans++; LastRequest = request;
        return Task.FromResult(Refuse ? new RuntimePlanResult(null, ["runtime.source_untrusted"])
            : new(new(TamperHost ? request with { HostId = "another-host" } : request, "sing-box",
                request.Change == RuntimeChange.Uninstall ? null : Release, Installation.Installation.ActiveReleaseId), [],
                request.Change == RuntimeChange.Uninstall || OmitDependencies ? null : new RuntimeDependencyReport(RuntimeArchiveDependencies.For(Release)
                    .Select(requirement => new RuntimeDependencyObservation(requirement.Kind, requirement.Identity,
                        RuntimeDependencyState.Satisfied, requirement.MinimumBytes, requirement.Identity, requirement.MinimumBytes))
                    .Select(item => TransformDependency?.Invoke(item) ?? item).ToArray())));
    }
}
