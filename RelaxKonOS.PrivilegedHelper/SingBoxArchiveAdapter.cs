using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.PrivilegedHelper;

internal sealed record SingBoxServiceObservation(bool Exists, bool Owned, bool Running, bool? BootEnabled,
    string? Executable, string? AppliedConfigurationIdentity);

/// <summary>Fixed, runtime-specific service implementation. Never bound to a client-provided executable or command.</summary>
internal interface ISingBoxArchiveHost
{
    Task<SingBoxServiceObservation> ObserveAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task ConfigureAsync(string verifiedBinary, string protectedConfiguration, string configurationIdentity, bool bootEnabled, CancellationToken ct);
    /// <summary>Changes only the start-on-boot registration of the already registered service; never starts it.</summary>
    Task SetBootAsync(bool enabled, CancellationToken ct);
    Task StartAsync(CancellationToken ct);
    Task RemoveOwnedRegistrationAsync(CancellationToken ct);
    Task<bool> ListenersHealthyAsync(ProxyServerConfiguration published, CancellationToken ct);
}

internal interface ISingBoxArchiveAuthorization
{
    /// <summary>Current Server-owned published state after credential/certificate authorization. No drafts or stale cached grant.</summary>
    Task<SingBoxCurrentAuthorization> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Real protected-file/native-binary adapter. A production factory must supply the fixed platform host,
/// fresh published authorization, OS protection, and use this state store's lock for all configuration writes.
/// </summary>
internal sealed class SingBoxArchiveAdapter(string protectedRoot, RuntimeArchiveStateStore stateStore, RuntimeSourceStore sources,
    IRuntimeArchiveTransport transport, ISingBoxArchiveHost host, ISingBoxArchiveAuthorization authorization,
    Action<string> protectDirectory, Func<InstallationStage, Task>? progress = null) : IRuntimeArchiveSwitchAdapter
{
    private readonly string root = ValidateRoot(protectedRoot);
    private string Configuration => Path.Combine(root, "config.json");
    private string Operations => Path.Combine(root, "operations");
    private string Versions => Path.Combine(root, "versions");
    private string Staging => Path.Combine(root, "staging");
    private static readonly ProxyServerMaterial Empty = new(new(0, []), [], []);

    public async Task PreflightAsync(RuntimeChangePlan plan, RuntimeArchiveState current, CancellationToken ct)
    {
        var release = plan.Release!;
        // Only the actually reviewed generator target is enabled here; version discovery is broader.
        if (release.RuntimeId != "sing-box" || !SingBoxNativeValidation.TryTag(release.OriginalVersion, out var version)
            || version.Normalized != SingBoxManifest.Version || release.ConfigurationCapability != "sing-box-1.14.3"
            || release.Target.Os != SingBoxManifest.PlatformName || release.Target.Architecture != SingBoxManifest.ArchitectureName
            || release.Target.InstallMode != RuntimeInstallMode.Archive
            || release.Target.ServiceHost != (OperatingSystem.IsWindows() ? "scm" : "systemd")
            || Path.GetFileName(release.Artifact.EntryPoint) != (OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box"))
            throw new RuntimePolicyException("runtime.configuration_unsupported");
        if (plan.Request.Change == RuntimeChange.Update && current.Active is { } active
            && SingBoxNativeValidation.UpdateProblem(active.MeasuredVersion, release.OriginalVersion) is { } problem)
            throw new RuntimePolicyException(problem);
        Authorize(plan);
        foreach (var directory in new[] { root, Operations, Versions, Staging }) Protect(directory);
        var service = await host.ObserveAsync(ct);
        if (service.Exists && (!service.Owned || current.Active is null || !SamePath(service.Executable, Binary(current.Active))))
            throw new RuntimePolicyException("runtime.external_installation");
        if (service.Exists && service.BootEnabled is null) throw new RuntimePolicyException("runtime.state_unknown");
        if (current.Active is not null) await VerifyFilesAsync(current.Active, ct);
        await CurrentAsync(ct);
    }

    public async Task<RuntimeServiceSnapshot> CaptureAsync(Guid operationId, RuntimeArchiveState current, CancellationToken ct)
    {
        var operation = Operation(operationId); Protect(operation);
        var service = await host.ObserveAsync(ct);
        if (service.Exists && (!service.Owned || service.BootEnabled is null || current.Active is null
            || !SamePath(service.Executable, Binary(current.Active)))) throw new RuntimePolicyException("runtime.state_unknown");
        await WriteNewAsync(Path.Combine(operation, "service.json"), JsonSerializer.SerializeToUtf8Bytes(service), ct);
        string? identity = null;
        if (File.Exists(Configuration))
        {
            var bytes = await ReadConfigurationAsync(Configuration, ct);
            identity = Hash(bytes);
            await WriteNewAsync(Path.Combine(operation, "previous.json"), bytes, ct);
        }
        return new(service.Running, service.BootEnabled ?? false, identity, identity is null ? null : operationId, operationId);
    }

    public async Task<RuntimePreparedSwitch> PrepareAsync(Guid operationId, RuntimeChangePlan plan, RuntimeServiceSnapshot before, CancellationToken ct)
    {
        var current = await CurrentAsync(ct);
        var operationStaging = Path.Combine(Staging, operationId.ToString("N")); Protect(operationStaging);
        var prepared = await new RuntimeArtifactPreparer(sources, transport, progress).PrepareAsync(plan.Release!.SourceId, plan.Release.ReleaseId,
            plan.Request.ExpectedCatalogRevision, plan.Request.ExpectedTrustRevision, plan.Release.Target,
            plan.Request.AllowPrerelease, operationStaging, ct);
        var revisionDirectory = Revision(operationId);
        if (Directory.Exists(revisionDirectory))
        { RuntimeArchiveExtractor.Discard(prepared.Archive, operationStaging); throw new RuntimePolicyException("runtime.staging_conflict"); }
        RuntimeSourceStore.RequireNoLinks(revisionDirectory);
        Directory.Move(prepared.Archive.Directory, revisionDirectory);
        Directory.Delete(operationStaging);
        var binary = Path.Combine(revisionDirectory, plan.Release.Artifact.EntryPoint.Replace('/', Path.DirectorySeparatorChar));
        var candidate = Path.Combine(Operation(operationId), "candidate.json");
        var rendered = Encoding.UTF8.GetBytes(SingBoxConfiguration.Render(current));
        await WriteNewAsync(candidate, rendered, ct);
        var build = await ValidateNativeAsync(binary, candidate, plan.Release, ct);
        return new(new(operationId, plan.Release, build.Version, Hash(rendered), plan.Request.ExpectedCatalogRevision,
            plan.Request.ExpectedTrustRevision), before.Running, before.BootEnabled);
    }

    public async Task ValidateSwitchAsync(RuntimePendingSwitch pending, CancellationToken ct)
    {
        Authorize(pending.Plan);
        var service = await host.ObserveAsync(ct);
        if (service.Exists && (!service.Owned || pending.Previous is null || !SamePath(service.Executable, Binary(pending.Previous)))
            || service.Running != pending.Snapshot!.Running || (service.BootEnabled ?? false) != pending.Snapshot.BootEnabled)
            throw new RuntimePolicyException("runtime.state_revision_conflict");
        await VerifyFilesAsync(pending.Prepared!.Revision, ct);
        var current = await CurrentAsync(ct);
        if (Hash(Encoding.UTF8.GetBytes(SingBoxConfiguration.Render(current))) != pending.Prepared.Revision.ConfigurationIdentity)
            throw new RuntimePolicyException("runtime.authorization_changed");
        await ValidateNativeAsync(Binary(pending.Prepared.Revision), Path.Combine(Operation(pending.OperationId), "candidate.json"),
            pending.Prepared.Revision.Release, ct);
    }

    public async Task ActivateAsync(RuntimePendingSwitch pending, CancellationToken ct)
    {
        await RequireCurrentConfigurationAsync(pending.Prepared!.Revision.ConfigurationIdentity, ct);
        await host.StopAsync(ct);
        var candidate = await ReadConfigurationAsync(Path.Combine(Operation(pending.OperationId), "candidate.json"), ct);
        if (Hash(candidate) != pending.Prepared!.Revision.ConfigurationIdentity) throw new RuntimePolicyException("runtime.configuration_drift");
        await ReplaceConfigurationAsync(candidate, ct);
        PrepareProgramReadAccess(pending.Prepared.Revision);
        await host.ConfigureAsync(Binary(pending.Prepared.Revision), Configuration, pending.Prepared.Revision.ConfigurationIdentity,
            pending.Prepared.BootEnabledAfterSwitch, ct);
        if (pending.Prepared.RunAfterSwitch)
        {
            // Native checks or service registration may outlive the earlier authorization lease.
            await RequireCurrentConfigurationAsync(pending.Prepared.Revision.ConfigurationIdentity, ct);
            await host.StartAsync(ct);
        }
    }

    public async Task<RuntimeActivationEvidence> VerifyAsync(RuntimePendingSwitch pending, CancellationToken ct)
    {
        var revision = pending.Prepared!.Revision;
        await VerifyFilesAsync(revision, ct);
        var native = await ValidateNativeAsync(Binary(revision), Configuration, revision.Release, ct);
        var identity = Hash(await ReadConfigurationAsync(Configuration, ct));
        var service = await host.ObserveAsync(ct);
        var current = await CurrentAsync(ct);
        var healthy = service.Exists && service.Owned && service.BootEnabled.HasValue && SamePath(service.Executable, Binary(revision))
            && Hash(Encoding.UTF8.GetBytes(SingBoxConfiguration.Render(current))) == identity
            && (!service.Running || service.AppliedConfigurationIdentity == identity && await host.ListenersHealthyAsync(current.Configuration, ct));
        return new(native.Version, identity, true, healthy, service.Running, service.BootEnabled ?? !pending.Prepared.BootEnabledAfterSwitch);
    }

    public async Task ValidateRestoreAsync(RuntimePendingSwitch pending, CancellationToken ct)
    {
        var current = await CurrentAsync(ct);
        if (pending.Snapshot!.ConfigurationIdentity is { } oldIdentity)
        {
            var previous = await ReadConfigurationAsync(Path.Combine(Operation(pending.OperationId), "previous.json"), ct);
            if (Hash(previous) != oldIdentity || Hash(Encoding.UTF8.GetBytes(SingBoxConfiguration.Render(current))) != oldIdentity)
                throw new RuntimePolicyException("runtime.authorization_changed");
        }
        if (pending.Previous is { } revision)
        {
            var source = sources.Read(revision.Release.SourceId) ?? throw new RuntimePolicyException("runtime.source_not_found");
            var permitted = sources.AuthorizeRelease(source.Source.SourceId, revision.Release.ReleaseId,
                source.Cached?.Catalog.Revision ?? 0, source.Source.TrustRevision, revision.Release.Target,
                revision.Release.Channel == RuntimeChannel.Prerelease, DateTimeOffset.UtcNow);
            if (!permitted.Release.Artifact.Sha256.Equals(revision.Release.Artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new RuntimePolicyException("runtime.release_untrusted");
            await VerifyFilesAsync(revision, ct);
            await ValidateNativeAsync(Binary(revision), Path.Combine(Operation(pending.OperationId), "previous.json"), revision.Release, ct);
        }
    }

    public Task StopForRecoveryAsync(RuntimePendingSwitch pending, CancellationToken ct) => host.StopAsync(ct);

    public async Task<RuntimeRestorationEvidence> RestoreAsync(RuntimePendingSwitch pending, CancellationToken ct)
    {
        var snapshot = pending.Snapshot!;
        if (snapshot.ConfigurationIdentity is { } oldIdentity) await RequireCurrentConfigurationAsync(oldIdentity, ct);
        var registration = JsonSerializer.Deserialize<SingBoxServiceObservation>(await File.ReadAllBytesAsync(Path.Combine(Operation(pending.OperationId), "service.json"), ct))
            ?? throw new RuntimePolicyException("runtime.snapshot_invalid");
        if (snapshot.ConfigurationIdentity is null) { RuntimeSourceStore.RequireNoLinks(Configuration); File.Delete(Configuration); }
        else await ReplaceConfigurationAsync(await ReadConfigurationAsync(Path.Combine(Operation(pending.OperationId), "previous.json"), ct), ct);
        if (!registration.Exists) await host.RemoveOwnedRegistrationAsync(ct);
        else
        {
            if (pending.Previous is null) throw new RuntimePolicyException("runtime.snapshot_invalid");
            await host.ConfigureAsync(Binary(pending.Previous), Configuration, snapshot.ConfigurationIdentity!, snapshot.BootEnabled, ct);
            if (snapshot.Running)
            {
                await RequireCurrentConfigurationAsync(snapshot.ConfigurationIdentity!, ct);
                await host.StartAsync(ct);
            }
        }
        var actual = await host.ObserveAsync(ct);
        var native = pending.Previous is null ? null : await ValidateNativeAsync(Binary(pending.Previous), Configuration, pending.Previous.Release, ct);
        var hash = File.Exists(Configuration) ? Hash(await ReadConfigurationAsync(Configuration, ct)) : null;
        var current = await CurrentAsync(ct);
        var healthy = actual.Exists == registration.Exists && (!actual.Exists || actual.Owned && actual.BootEnabled.HasValue && pending.Previous is not null
            && SamePath(actual.Executable, Binary(pending.Previous)))
            && (!actual.Running || actual.AppliedConfigurationIdentity == hash && await host.ListenersHealthyAsync(current.Configuration, ct));
        return new(native?.Version, hash, actual.Running, actual.BootEnabled ?? false, healthy, healthy);
    }

    public Task CleanupPreparationAsync(RuntimePendingSwitch pending, CancellationToken ct)
    {
        var path = Revision(pending.OperationId);
        if (stateStore.Read()?.Active?.RevisionId == pending.OperationId) throw new RuntimePolicyException("runtime.active_revision_cleanup_forbidden");
        DeleteOwnedRevision(path);
        var staging = Path.GetFullPath(Path.Combine(Staging, pending.OperationId.ToString("N")));
        if (Path.GetDirectoryName(staging) != Path.GetFullPath(Staging)) throw new RuntimePolicyException("runtime.staging_path_invalid");
        if (Directory.Exists(staging)) DeleteTree(staging);
        return Task.CompletedTask;
    }

    private AuthorizedRuntimeRelease Authorize(RuntimeChangePlan plan)
    {
        var release = plan.Release!;
        var authorized = sources.AuthorizeRelease(release.SourceId, release.ReleaseId,
            plan.Request.ExpectedCatalogRevision, plan.Request.ExpectedTrustRevision,
            release.Target, plan.Request.AllowPrerelease, DateTimeOffset.UtcNow);
        if (!RuntimeJson.Serialize(release).AsSpan().SequenceEqual(RuntimeJson.Serialize(authorized.CatalogRelease)))
            throw new RuntimePolicyException("runtime.release_untrusted");
        return authorized;
    }

    private async Task<ProxyServerMaterial> CurrentAsync(CancellationToken ct)
    {
        var grant = await authorization.ReadAsync(ct);
        if (grant.ValidUntil <= DateTimeOffset.UtcNow || grant.ValidUntil > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new RuntimePolicyException("runtime.authorization_unavailable");
        return grant.Published ?? Empty;
    }

    private async Task RequireCurrentConfigurationAsync(string identity, CancellationToken ct)
    {
        var current = await CurrentAsync(ct);
        if (Hash(Encoding.UTF8.GetBytes(SingBoxConfiguration.Render(current))) != identity)
            throw new RuntimePolicyException("runtime.authorization_changed");
    }

    private async Task<SingBoxBuildEvidence> ValidateNativeAsync(string binary, string configuration, RuntimeRelease release, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var build = SingBoxNativeValidation.Parse(await SingBoxOperations.RunAsync(binary, ["version"]), release.OriginalVersion,
            release.Target.Os, release.Target.Architecture);
        SingBoxNativeValidation.RequireConfigurationFeatures(build, await ReadConfigurationAsync(configuration, ct));
        await SingBoxOperations.RunAsync(binary, ["check", "-c", configuration]);
        ct.ThrowIfCancellationRequested();
        return build;
    }

    private async Task VerifyFilesAsync(RuntimeStoredRevision revision, CancellationToken ct)
    {
        foreach (var file in revision.Release.Artifact.Layout.Files)
        {
            var path = Path.Combine(Revision(revision.RevisionId), file.Path.Replace('/', Path.DirectorySeparatorChar));
            RuntimeSourceStore.RequireNoLinks(path);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != file.Length || !Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new RuntimePolicyException("runtime.installed_file_drift");
        }
    }

    private void PrepareProgramReadAccess(RuntimeStoredRevision revision)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        File.SetUnixFileMode(Versions, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        foreach (var file in revision.Release.Artifact.Layout.Files)
        {
            var path = Path.Combine(Revision(revision.RevisionId), file.Path.Replace('/', Path.DirectorySeparatorChar));
            RuntimeSourceStore.RequireNoLinks(path);
            for (var directory = Path.GetDirectoryName(path)!; directory != Versions; directory = Path.GetDirectoryName(directory)!)
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                | (file.Executable ? UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute : 0));
        }
    }

    private string Operation(Guid id) => Path.Combine(Operations, id.ToString("N"));
    private static string ValidateRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new RuntimePolicyException("runtime.staging_path_invalid");
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (full == Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar))
            throw new RuntimePolicyException("runtime.staging_path_invalid");
        return full;
    }
    private string Revision(Guid id) => Path.Combine(Versions, id.ToString("N"));
    private string Binary(RuntimeStoredRevision revision) => Path.Combine(Revision(revision.RevisionId), revision.Release.Artifact.EntryPoint.Replace('/', Path.DirectorySeparatorChar));
    private void Protect(string path) { RuntimeSourceStore.RequireNoLinks(path); protectDirectory(path); RuntimeSourceStore.RequireNoLinks(path); }
    private static bool SamePath(string? left, string right) => left is not null && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static async Task<byte[]> ReadConfigurationAsync(string path, CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 4 * 1024 * 1024) throw new RuntimePolicyException("runtime.native_configuration_invalid");
        var bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes, ct); return bytes;
    }
    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var output = new FileStream(path, options); await output.WriteAsync(bytes, ct); output.Flush(true);
    }
    private async Task ReplaceConfigurationAsync(byte[] bytes, CancellationToken ct)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".configuration");
        try { await WriteNewAsync(path, bytes, ct); RuntimeSourceStore.RequireNoLinks(Configuration); File.Move(path, Configuration, true); }
        finally { File.Delete(path); }
    }
    private void DeleteOwnedRevision(string path)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) != Path.GetFullPath(Versions)) throw new RuntimePolicyException("runtime.staging_path_invalid");
        if (!Directory.Exists(path)) return;
        RuntimeSourceStore.RequireNoLinks(path);
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            RuntimeSourceStore.RequireNoLinks(child);
            if (Directory.Exists(child)) DeleteTree(child); else File.Delete(child);
        }
        Directory.Delete(path);
    }
    private static void DeleteTree(string path)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        { RuntimeSourceStore.RequireNoLinks(child); if (Directory.Exists(child)) DeleteTree(child); else File.Delete(child); }
        Directory.Delete(path);
    }
}
