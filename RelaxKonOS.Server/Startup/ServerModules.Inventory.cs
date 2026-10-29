using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.Startup;

// Read-only diagnostics for operators and clients: what is listed, what would block a disable, and
// what residency state each enabled module is in. None of these methods may change state; in
// particular a status query never loads a module.
internal sealed partial class ServerModules
{
    /// <summary>
    /// Read-only inventory for operators and clients. It answers "what would happen if I disabled
    /// this module" with the same facts the startup gate uses.
    /// </summary>
    internal async Task<IReadOnlyList<ServerModuleDto>> DescribeAsync(IServiceProvider services,
        ServerModuleContext context, CancellationToken cancellationToken)
    {
        var all = _enabled.Concat(_disabled).ToArray();
        var enabled = _enabled.ToHashSet();
        // Reading residency must not load a module: the coordinator answers from its own state only.
        var residency = services.GetService(typeof(ModuleResidencyCoordinator)) as ModuleResidencyCoordinator;
        var inventory = new List<ServerModuleDto>(all.Length);
        foreach (var module in all)
        {
            var isEnabled = enabled.Contains(module);
            // A disabled module cannot be holding live work: that would have failed the startup gate.
            IReadOnlyList<ServerModuleOrphanedWork> work;
            if (!isEnabled || module is not IServerModuleDeactivationGuard guard)
                work = [];
            else
            {
                try { work = await guard.InspectOrphanedWorkAsync(services, context, cancellationToken); }
                catch (Exception exception)
                {
                    // A diagnostic route must not fail because one guard is unhappy, and it must not
                    // report "safe to disable" either. Reporting the failure is the fail-closed answer.
                    work = [new(module.Id, $"the deactivation guard failed ({exception.GetType().Name}); "
                        + "treat this module as not safe to disable until the cause is understood.")];
                }
            }
            // Only an enabled dependent blocks a disable; a disabled one is already out of the process.
            var requiredBy = all
                .Where(other => enabled.Contains(other) && other.Requires.Contains(module.Id, StringComparer.Ordinal))
                .Select(other => other.Id)
                .ToArray();
            inventory.Add(new ServerModuleDto(module.Id, isEnabled, module.Capabilities, module.Requires,
                requiredBy, work.Select(item => item.Description).ToArray(),
                isEnabled ? DescribeResidency(module.Id, residency) : null));
        }
        return inventory;
    }

    /// <summary>
    /// Residency as the process sees it. A module without a release boundary is reported as
    /// <c>resident</c> instead of an empty object: "not releasable" is a fact, not an unknown.
    /// </summary>
    private static ServerModuleResidencyDto DescribeResidency(string moduleId, ModuleResidencyCoordinator? residency)
    {
        var snapshot = residency?.Describe(moduleId) ?? ModuleResidencySnapshot.Resident(moduleId);
        return new ServerModuleResidencyDto(
            snapshot.State.ToString().ToLowerInvariant(),
            snapshot.Releasable,
            snapshot.Strategy.ToString(),
            (int)snapshot.IdleGracePeriod.TotalSeconds,
            snapshot.ActiveLeases,
            snapshot.LastUsedAt,
            snapshot.LastWakeMilliseconds,
            snapshot.BlockedBy,
            snapshot.Failure,
            snapshot.LastReleasedAt);
    }

    /// <summary>
    /// The deactivation gate. A module that owns durable work is asked what it would leave unresolved
    /// while it is not part of this process; anything still queued, running or unknown stops startup
    /// with the operation IDs an operator needs. This deliberately has no override: recovering or
    /// finishing the work is the only way past it, and clearing a ledger to satisfy the gate would be
    /// exactly the silent data loss this design forbids.
    /// </summary>
    internal async Task ValidateDeactivationAsync(IServiceProvider services, ServerModuleContext context,
        CancellationToken cancellationToken)
    {
        var orphaned = new List<ServerModuleOrphanedWork>();
        foreach (var module in _disabled.OfType<IServerModuleDeactivationGuard>())
            orphaned.AddRange(await module.InspectOrphanedWorkAsync(services, context, cancellationToken));

        if (orphaned.Count == 0) return;
        var details = string.Join(" ", orphaned.Select(item => $"({item.ModuleId}) {item.Description}"));
        var modules = string.Join(", ", orphaned.Select(item => item.ModuleId).Distinct(StringComparer.Ordinal));
        throw new InvalidOperationException(
            $"Refusing to start with unresolved durable work because {modules} is disabled: {details} " +
            "Re-enable the module, let it reconcile or finish the operation through its normal recovery entry, then disable it again.");
    }
}
