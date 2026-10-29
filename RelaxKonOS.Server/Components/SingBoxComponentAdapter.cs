using RelaxKonOS.Server.ManagedRuntimes;

namespace RelaxKonOS.Server.Components;

/// <summary>Uses the sing-box domain ledger and frozen plan without adding an execution path.</summary>
public sealed class SingBoxComponentAdapter(SingBoxRuntimeService runtime)
    : ArchiveComponentAdapter(RelaxKonOS.Protocol.ManagedRuntimes.ComponentIds.SingBox,
        ct => runtime.QueryAsync(ct), runtime.PlanAsync);
