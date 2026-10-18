using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Removes the one Helper-owned Mihomo installation. It only deletes resources under the protected
/// instance root, never touches a package manager, and keeps configuration and secrets unless the
/// request explicitly abandoned retention.
/// </summary>
internal sealed class MihomoRemovalAdapter(string protectedRoot, IMihomoArchiveHost host,
    IMihomoArchiveAuthorization authorization, RuntimeSourceStore sources, Action<string> protectDirectory) : IRuntimeArchiveRemovalAdapter
{
    private readonly string root = ValidateRoot(protectedRoot);
    private string Configuration => Path.Combine(root, "active.yaml");
    private string PreviousConfiguration => Path.Combine(root, "active.yaml.previous");
    private string Operations => Path.Combine(root, "operations");
    private string Versions => Path.Combine(root, "versions");
    private string Staging => Path.Combine(root, "staging");
    private string DataRoot => Path.Combine(root, "data");

    public async Task PreflightAsync(RuntimeChangePlan plan, RuntimeArchiveState current, CancellationToken ct)
    {
        if (plan.Request.Change != RuntimeChange.Uninstall || plan.Release is not null || plan.Request.ReleaseId is not null)
            throw new RuntimePolicyException("runtime.action_unsupported");
        if (current.Active is not { } active || current.Installation.Ownership != RuntimeOwnership.Managed)
            throw new RuntimePolicyException("runtime.not_managed");
        if (plan.RuntimeId != "mihomo" || active.Release.RuntimeId != "mihomo")
            throw new RuntimePolicyException("runtime.installation_mismatch");
        foreach (var directory in new[] { root, Operations, Versions, Staging }) Protect(directory);
        await VerifyFilesAsync(active, ct);
        var service = await host.ObserveAsync(ct);
        if (service.Exists && (!service.Owned || service.BootEnabled is null || !SamePath(service.Executable, Binary(active))
            || !SamePath(service.DataDirectory, Data(active))))
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
            var published = await RequireCurrentAsync(snapshot, ct);
            if (Hash(await ReadConfigurationAsync(Configuration, ct)) != snapshot.ConfigurationIdentity) return false;
            var source = sources.Read(active.Release.SourceId) ?? throw new RuntimePolicyException("runtime.source_not_found");
            var permitted = sources.AuthorizeRelease(source.Source.SourceId, active.Release.ReleaseId,
                source.Cached?.Catalog.Revision ?? 0, source.Source.TrustRevision, active.Release.Target,
                active.Release.Channel == RuntimeChannel.Prerelease, DateTimeOffset.UtcNow);
            if (!permitted.Release.Artifact.Sha256.Equals(active.Release.Artifact.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            var build = MihomoNativeValidation.Parse(await MihomoOperations.VersionAsync(Binary(active)),
                active.Release.OriginalVersion, active.Release.Target.Os, active.Release.Target.Architecture);
            if (!published.RequiredBuildTags.All(build.BuildTags.Contains)) return false;
            await MihomoOperations.TestConfigurationAsync(Binary(active), Data(active), Configuration);
            await host.ConfigureAsync(Binary(active), Data(active), Configuration, snapshot.ConfigurationIdentity!, snapshot.BootEnabled, ct);
            if (snapshot.Running)
            {
                await RequireCurrentAsync(snapshot, ct);
                await host.StartAsync(ct);
            }
            var service = await host.ObserveAsync(ct);
            return service.Exists && service.Owned && SamePath(service.Executable, Binary(active))
                && SamePath(service.DataDirectory, Data(active)) && service.BootEnabled == snapshot.BootEnabled && service.Running == snapshot.Running
                && (!service.Running || service.AppliedConfigurationIdentity == snapshot.ConfigurationIdentity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or RuntimePolicyException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private async Task<MihomoPublishedRuntime> RequireCurrentAsync(RuntimeServiceSnapshot snapshot, CancellationToken ct)
    {
        var grant = await authorization.ReadAsync(ct);
        if (grant.ValidUntil <= DateTimeOffset.UtcNow || grant.ValidUntil > DateTimeOffset.UtcNow.AddMinutes(5)
            || grant.Published is not { } published || published.NetworkTransitionRequired)
            throw new RuntimePolicyException("runtime.authorization_changed");
        MihomoRuntimeMaterial.Validate(published);
        if (MihomoConfigurationPolicy.Validate(published).Material.NetworkTransitionRequired)
            throw new RuntimePolicyException("runtime.network_transaction_required");
        if (Hash(Encoding.UTF8.GetBytes(published.Yaml)) != snapshot.ConfigurationIdentity)
            throw new RuntimePolicyException("runtime.authorization_changed");
        return published;
    }

    /// <summary>Program revisions, staging leftovers, and operation backups are owned resources.</summary>
    public async Task CleanupProgramAsync(RuntimeArchiveState current, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (current.Removal!.Plan.Request.KeepData)
        {
            // Record the provenance before deleting any program. Retained data directories are never
            // renamed while the removal journal may need to reconcile a crash at this boundary.
            var active = current.Active ?? throw new RuntimePolicyException("runtime.not_managed");
            RuntimeSourceStore.RequireNoLinks(Data(active));
            if (!Directory.Exists(Data(active))) throw new RuntimePolicyException("runtime.data_snapshot_missing");
            await MihomoRuntimeData.RetainAsync(root, active, ct);
        }
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
        var candidate = Path.Combine(root, "candidate.yaml");
        RuntimeSourceStore.RequireNoLinks(candidate);
        if (File.Exists(candidate)) File.Delete(candidate);
        foreach (var directory in new[] { DataRoot, Path.Combine(root, "seed-data") })
            if (Directory.Exists(directory)) DeleteTree(directory);
        var retained = Path.Combine(root, "retained-data.json");
        RuntimeSourceStore.RequireNoLinks(retained);
        File.Delete(retained);
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
    private string Data(RuntimeStoredRevision revision) => Path.Combine(DataRoot, revision.RevisionId.ToString("N"));
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
