using System.Security.Cryptography;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Removes the one Helper-owned sing-box installation. It only deletes resources under the protected
/// instance root, never touches a package manager, and keeps configuration and secrets unless the
/// request explicitly abandoned retention.
/// </summary>
internal sealed class SingBoxRemovalAdapter(string protectedRoot, ISingBoxArchiveHost host,
    Action<string> protectDirectory) : IRuntimeArchiveRemovalAdapter
{
    private readonly string root = ValidateRoot(protectedRoot);
    private string Configuration => Path.Combine(root, "config.json");
    private string PreviousConfiguration => Path.Combine(root, "config.json.previous");
    private string Operations => Path.Combine(root, "operations");
    private string Versions => Path.Combine(root, "versions");
    private string Staging => Path.Combine(root, "staging");

    public async Task PreflightAsync(RuntimeChangePlan plan, RuntimeArchiveState current, CancellationToken ct)
    {
        if (plan.Request.Change != RuntimeChange.Uninstall || plan.Release is not null || plan.Request.ReleaseId is not null)
            throw new RuntimePolicyException("runtime.action_unsupported");
        if (current.Active is not { } active || current.Installation.Ownership != RuntimeOwnership.Managed)
            throw new RuntimePolicyException("runtime.not_managed");
        foreach (var directory in new[] { root, Operations, Versions, Staging }) Protect(directory);
        await VerifyFilesAsync(active, ct);
        var service = await host.ObserveAsync(ct);
        if (service.Exists && (!service.Owned || service.BootEnabled is null || !SamePath(service.Executable, Binary(active))))
            throw new RuntimePolicyException("runtime.external_installation");
        if (service.Exists && service.Running && service.AppliedConfigurationIdentity != current.ConfigurationIdentity)
            throw new RuntimePolicyException("runtime.configuration_drift");
    }

    public async Task<RuntimeServiceSnapshot> CaptureAsync(Guid operationId, RuntimeArchiveState current, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var service = await host.ObserveAsync(ct);
        string? identity = null;
        if (File.Exists(Configuration))
        {
            var bytes = await ReadConfigurationAsync(Configuration, ct);
            identity = Hash(bytes);
        }
        if (identity != current.ConfigurationIdentity) throw new RuntimePolicyException("runtime.configuration_drift");
        return new(service.Running, service.BootEnabled ?? false, identity,
            identity is null ? null : operationId, operationId);
    }

    public async Task RemoveAsync(RuntimeArchiveState current, RuntimeServiceSnapshot snapshot, CancellationToken ct)
    {
        await host.StopAsync(ct);
        await host.RemoveOwnedRegistrationAsync(ct);
    }

    public async Task<bool> VerifyRemovedAsync(CancellationToken ct)
    {
        var service = await host.ObserveAsync(ct);
        return !service.Exists && !service.Running;
    }

    public async Task<bool> RestoreAsync(RuntimeArchiveState current, RuntimeServiceSnapshot snapshot, CancellationToken ct)
    {
        if (current.Active is not { } active) return false;
        try
        {
            RuntimeSourceStore.RequireNoLinks(Configuration);
            if (!File.Exists(Configuration)) return false;
            await VerifyFilesAsync(active, ct);
            await host.ConfigureAsync(Binary(active), Configuration, snapshot.ConfigurationIdentity!, snapshot.BootEnabled, ct);
            if (snapshot.Running) await host.StartAsync(ct);
            var service = await host.ObserveAsync(ct);
            return service.Exists && service.Owned && SamePath(service.Executable, Binary(active))
                && service.Running == snapshot.Running
                && (!service.Running || service.AppliedConfigurationIdentity == snapshot.ConfigurationIdentity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or RuntimePolicyException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    /// <summary>Program revisions, staging leftovers, and operation backups are owned resources.</summary>
    public Task CleanupProgramAsync(RuntimeArchiveState current, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(Versions))
        {
            RuntimeSourceStore.RequireNoLinks(Versions);
            foreach (var child in Directory.EnumerateFileSystemEntries(Versions).ToArray())
            {
                RuntimeSourceStore.RequireNoLinks(child);
                if (Path.GetDirectoryName(Path.GetFullPath(child)) != Path.GetFullPath(Versions))
                    throw new RuntimePolicyException("runtime.staging_path_invalid");
                if (Directory.Exists(child)) DeleteTree(child); else File.Delete(child);
            }
            // The empty revision root goes too; preparation re-creates it under the same protection.
            if (!Directory.EnumerateFileSystemEntries(Versions).Any()) Directory.Delete(Versions);
        }
        if (Directory.Exists(Staging)) DeleteTree(Staging);
        if (Directory.Exists(Operations)) DeleteTree(Operations);
        return Task.CompletedTask;
    }

    /// <summary>Only executed after explicit data deletion; the publication identity leaves the state with it.</summary>
    public Task DeleteDataAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var path in new[] { Configuration, PreviousConfiguration })
        {
            RuntimeSourceStore.RequireNoLinks(path);
            if (File.Exists(path)) File.Delete(path);
        }
        var candidate = Path.Combine(root, "candidate.json");
        RuntimeSourceStore.RequireNoLinks(candidate);
        if (File.Exists(candidate)) File.Delete(candidate);
        return Task.CompletedTask;
    }

    private async Task VerifyFilesAsync(RuntimeStoredRevision revision, CancellationToken ct)
    {
        foreach (var file in revision.Release.Artifact.Layout.Files)
        {
            var path = Path.Combine(Revision(revision.RevisionId), file.Path.Replace('/', Path.DirectorySeparatorChar));
            RuntimeSourceStore.RequireNoLinks(path);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != file.Length
                || !Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new RuntimePolicyException("runtime.installed_file_drift");
        }
    }

    private void Protect(string path) { RuntimeSourceStore.RequireNoLinks(path); protectDirectory(path); RuntimeSourceStore.RequireNoLinks(path); }
    private string Revision(Guid id) => Path.Combine(Versions, id.ToString("N"));
    private string Binary(RuntimeStoredRevision revision) =>
        Path.Combine(Revision(revision.RevisionId), revision.Release.Artifact.EntryPoint.Replace('/', Path.DirectorySeparatorChar));
    private static bool SamePath(string? left, string right) => left is not null
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static async Task<byte[]> ReadConfigurationAsync(string path, CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 4 * 1024 * 1024) throw new RuntimePolicyException("runtime.native_configuration_invalid");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, ct);
        return bytes;
    }

    private static void DeleteTree(string path)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        { RuntimeSourceStore.RequireNoLinks(child); if (Directory.Exists(child)) DeleteTree(child); else File.Delete(child); }
        Directory.Delete(path);
    }

    private static string ValidateRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new RuntimePolicyException("runtime.staging_path_invalid");
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (full == Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar))
            throw new RuntimePolicyException("runtime.staging_path_invalid");
        return full;
    }
}
