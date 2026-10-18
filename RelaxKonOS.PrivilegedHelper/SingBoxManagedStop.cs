using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ProxyServer;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Called only while holding the archive instance lock. Stopping requires ownership, not a grant to run credentials.</summary>
internal static class SingBoxManagedStop
{
    internal static async Task<ProxyServerStatus> ExecuteAsync(string root, RuntimeArchiveState state,
        ISingBoxArchiveHost host, CancellationToken ct)
    {
        var before = await host.ObserveAsync(ct);
        if (before.Exists)
        {
            // A pending switch may have registered the candidate before committing the active pointer.
            // Neither a matching service name nor a root-local arbitrary executable grants ownership.
            if (!before.Owned || !state.Revisions.Any(revision => SamePath(before.Executable,
                    Path.Combine(root, "versions", revision.RevisionId.ToString("N"),
                        revision.Release.Artifact.EntryPoint.Replace('/', Path.DirectorySeparatorChar)))))
                throw new RuntimePolicyException("runtime.external_installation");
            await host.StopAsync(ct);
        }
        var after = await host.ObserveAsync(ct);
        if (after.Running || after.Exists && (!after.Owned || !SamePath(before.Executable, after.Executable)))
            throw new RuntimePolicyException("runtime.stop_not_verified");
        // Preserve all pending checkpoints, snapshots and receipts. A stop is not a recovery success.
        return new(true, state.Active is not null, false, state.Pending is not null,
            state.Active?.MeasuredVersion, state.ConfigurationIdentity, false,
            state.Pending is null ? null : "proxy_server.recovery_required", BootEnabled: after.BootEnabled);
    }

    private static bool SamePath(string? left, string? right) => left is not null && right is not null
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
