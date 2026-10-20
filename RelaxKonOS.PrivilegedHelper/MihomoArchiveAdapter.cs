using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.PrivilegedHelper;

internal sealed record MihomoServiceObservation(bool Exists, bool Owned, bool Running, bool? BootEnabled,
    string? Executable, string? AppliedConfigurationIdentity, string? DataDirectory);

/// <summary>Fixed, runtime-specific service implementation. Never bound to a client-provided executable or command.</summary>
internal interface IMihomoArchiveHost
{
    Task<MihomoServiceObservation> ObserveAsync(CancellationToken ct);
    /// <summary>Fixed-unit ownership and stopped-state proof only; does not claim program/configuration health.</summary>
    Task<RuntimeServiceObservation> ObserveRegistrationAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task ConfigureAsync(string verifiedBinary, string protectedDataDirectory, string protectedConfiguration,
        string configurationIdentity, bool bootEnabled, CancellationToken ct);
    /// <summary>Changes only the start-on-boot registration of the already registered service; never starts it.</summary>
    Task SetBootAsync(bool enabled, CancellationToken ct);
    Task StartAsync(CancellationToken ct);
    Task RemoveOwnedRegistrationAsync(CancellationToken ct);
    Task<bool> ListenersHealthyAsync(MihomoPublishedRuntime published, CancellationToken ct);
}

internal interface IMihomoArchiveAuthorization
{
    /// <summary>Current Server-owned published state after credential/certificate authorization. No drafts or stale cached grant.</summary>
    Task<MihomoCurrentAuthorization> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Real protected-file/native-binary adapter. A production factory must supply the fixed platform host,
/// fresh published authorization, OS protection, and use this state store's lock for all configuration writes.
/// </summary>
internal sealed class MihomoArchiveAdapter(string protectedRoot, RuntimeArchiveStateStore stateStore, RuntimeSourceStore sources,
    IRuntimeArchiveTransport transport, IMihomoArchiveHost host, IMihomoArchiveAuthorization authorization,
    Action<string> protectDirectory, RuntimeStageReporter? progress = null, IRuntimeDependencyEnvironment? environment = null)
    : IRuntimeArchiveSwitchAdapter
{
    private readonly IRuntimeDependencyEnvironment environment = environment ?? new HostDependencyEnvironment();
    private readonly string root = ValidateRoot(protectedRoot);
    private string Configuration => Path.Combine(root, "active.yaml");
    private string Operations => Path.Combine(root, "operations");
    private string Versions => Path.Combine(root, "versions");
    private string Staging => Path.Combine(root, "staging");
    private string DataRoot => Path.Combine(root, "data");
    private string SeedData => Path.Combine(root, "seed-data");

    public async Task PreflightAsync(RuntimeChangePlan plan, RuntimeArchiveState current, CancellationToken ct)
    {
        var release = plan.Release!;
        // Only the measured native-format range is enabled here; discovery is broader and the catalog
        // publisher cannot choose the capability string.
        MihomoRuntimePreparation.RequireSupportedRelease(release);
        if (plan.Request.Change == RuntimeChange.Update && current.Active is { } active
            && MihomoNativeValidation.UpdateProblem(active.MeasuredVersion, release.OriginalVersion) is { } problem)
            throw new RuntimePolicyException(problem);
        Authorize(plan);
        foreach (var directory in new[] { root, Operations, Versions, Staging, DataRoot, SeedData }) Protect(directory);
        var service = await host.ObserveAsync(ct);
        if (service.Exists && (!service.Owned || current.Active is null || !SamePath(service.Executable, Binary(current.Active))
            || !SamePath(service.DataDirectory, Data(current.Active.RevisionId))))
            throw new RuntimePolicyException("runtime.external_installation");
        if (service.Exists && service.BootEnabled is null) throw new RuntimePolicyException("runtime.state_unknown");
        if (current.Active is not null)
        {
            await VerifyFilesAsync(current.Active, ct);
            RuntimeSourceStore.RequireNoLinks(Data(current.Active.RevisionId));
            if (!Directory.Exists(Data(current.Active.RevisionId))) throw new RuntimePolicyException("runtime.data_snapshot_missing");
        }
        await CurrentAsync(ct);
        if (plan.Request.Change == RuntimeChange.Install) MihomoRuntimeData.RetainedSource(root, release);
        // The design checks dependencies and the published configuration together before anything is
        // downloaded. Reserve space for a bounded, independent data revision as well as the archive.
        var requirements = Requirements(release);
        var report = RuntimeDependencyProbe.For(requirements, environment, root);
        if (!report.Satisfies(requirements)) throw new RuntimePolicyException("runtime.dependencies_unsatisfied");
    }

    internal static IReadOnlyList<RuntimeDependencyRequirement> Requirements(RuntimeRelease release) =>
        RuntimeArchiveDependencies.For(release).Select(r => r.Kind == RuntimeDependencyKind.DiskSpace
            ? r with { MinimumBytes = checked(r.MinimumBytes!.Value + MihomoRuntimeData.MaximumBytes) } : r).ToArray();

    public async Task<RuntimeServiceSnapshot> CaptureAsync(Guid operationId, RuntimeArchiveState current, CancellationToken ct)
    {
        var operation = Operation(operationId); Protect(operation);
        var service = await host.ObserveAsync(ct);
        if (service.Exists && (!service.Owned || service.BootEnabled is null || current.Active is null
            || !SamePath(service.Executable, Binary(current.Active)) || !SamePath(service.DataDirectory, Data(current.Active.RevisionId))))
            throw new RuntimePolicyException("runtime.state_unknown");
        await WriteNewAsync(Path.Combine(operation, "service.json"), JsonSerializer.SerializeToUtf8Bytes(service), ct);
        string? identity = null;
        if (File.Exists(Configuration))
        {
            var bytes = await ReadConfigurationAsync(Configuration, ct);
            identity = Hash(bytes);
            await WriteNewAsync(Path.Combine(operation, "previous.yaml"), bytes, ct);
        }
        return new(service.Running, service.BootEnabled ?? false, identity, identity is null ? null : operationId, operationId);
    }

    public async Task<RuntimePreparedSwitch> PrepareAsync(Guid operationId, RuntimeChangePlan plan, RuntimeServiceSnapshot before, CancellationToken ct)
    {
        var current = await CurrentAsync(ct);
        var operationStaging = Path.Combine(Staging, operationId.ToString("N")); Protect(operationStaging);
        var candidate = Path.Combine(Operation(operationId), "candidate.yaml");
        var rendered = Encoding.UTF8.GetBytes(current.Yaml);
        await WriteNewAsync(candidate, rendered, ct);
        var dataSource = RollbackDataSource(plan, operationId)
            ?? (plan.Request.Change == RuntimeChange.Install ? MihomoRuntimeData.RetainedSource(root, plan.Release!) : null);
        await MihomoRuntimeData.CopyAsync(dataSource ?? SeedData, Data(operationId), protectDirectory, ct);
        var preparedRuntime = await new MihomoRuntimePreparation(sources, transport, progress)
            .PrepareAsync(plan, operationStaging, Data(operationId), candidate, ct);
        var prepared = preparedRuntime.Artifact;
        RequireFeatures(preparedRuntime.Build, current);
        var revisionDirectory = Revision(operationId);
        if (Directory.Exists(revisionDirectory))
        { RuntimeArchiveExtractor.Discard(prepared.Archive, operationStaging); throw new RuntimePolicyException("runtime.staging_conflict"); }
        RuntimeSourceStore.RequireNoLinks(revisionDirectory);
        Directory.Move(prepared.Archive.Directory, revisionDirectory);
        Directory.Delete(operationStaging);
        return new(new(operationId, plan.Release!, preparedRuntime.Build.Version, Hash(rendered), plan.Request.ExpectedCatalogRevision,
            plan.Request.ExpectedTrustRevision), before.Running, before.BootEnabled);
    }

    public async Task ValidateSwitchAsync(RuntimePendingSwitch pending, CancellationToken ct)
    {
        Authorize(pending.Plan);
        var service = await host.ObserveAsync(ct);
        if (service.Exists && (!service.Owned || pending.Previous is null || !SamePath(service.Executable, Binary(pending.Previous))
            || !SamePath(service.DataDirectory, Data(pending.Previous.RevisionId)))
            || service.Running != pending.Snapshot!.Running || (service.BootEnabled ?? false) != pending.Snapshot.BootEnabled)
            throw new RuntimePolicyException("runtime.state_revision_conflict");
        await VerifyFilesAsync(pending.Prepared!.Revision, ct);
        var current = await CurrentAsync(ct);
        if (Hash(Encoding.UTF8.GetBytes(current.Yaml)) != pending.Prepared.Revision.ConfigurationIdentity)
            throw new RuntimePolicyException("runtime.authorization_changed");
        await ValidateNativeAsync(Binary(pending.Prepared.Revision), Path.Combine(Operation(pending.OperationId), "candidate.yaml"),
            pending.Prepared.Revision.Release, Data(pending.OperationId), ct);
    }

    public async Task ActivateAsync(RuntimePendingSwitch pending, CancellationToken ct)
    {
        await RequireCurrentConfigurationAsync(pending.Prepared!.Revision.ConfigurationIdentity, ct);
        await host.StopAsync(ct);
        // Only copy mutable files after the owned service stopped. A failed target never writes into
        // the old data revision. Explicit rollback uses its retained target data, not a newer database.
        var stopped = await host.ObserveAsync(ct);
        if (stopped.Running) throw new RuntimePolicyException("runtime.state_unknown");
        if (pending.Previous is { } previous && pending.Plan.Request.Change != RuntimeChange.Rollback)
        {
            DeleteDataRevision(pending.OperationId);
            await MihomoRuntimeData.CopyAsync(Data(previous.RevisionId), Data(pending.OperationId), protectDirectory, ct);
        }
        var candidate = await ReadConfigurationAsync(Path.Combine(Operation(pending.OperationId), "candidate.yaml"), ct);
        if (Hash(candidate) != pending.Prepared!.Revision.ConfigurationIdentity) throw new RuntimePolicyException("runtime.configuration_drift");
        await ValidateNativeAsync(Binary(pending.Prepared.Revision), Path.Combine(Operation(pending.OperationId), "candidate.yaml"),
            pending.Prepared.Revision.Release, Data(pending.OperationId), ct);
        await RequireCurrentConfigurationAsync(pending.Prepared.Revision.ConfigurationIdentity, ct);
        await ReplaceConfigurationAsync(candidate, ct);
        PrepareProgramReadAccess(pending.Prepared.Revision);
        await host.ConfigureAsync(Binary(pending.Prepared.Revision), Data(pending.OperationId), Configuration, pending.Prepared.Revision.ConfigurationIdentity,
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
        var native = await ValidateNativeAsync(Binary(revision), Configuration, revision.Release, Data(revision.RevisionId), ct);
        var identity = Hash(await ReadConfigurationAsync(Configuration, ct));
        var service = await host.ObserveAsync(ct);
        var current = await CurrentAsync(ct);
        var healthy = service.Exists && service.Owned && service.BootEnabled.HasValue && SamePath(service.Executable, Binary(revision))
            && SamePath(service.DataDirectory, Data(revision.RevisionId))
            && Hash(Encoding.UTF8.GetBytes(current.Yaml)) == identity
            && (!service.Running || service.AppliedConfigurationIdentity == identity && await host.ListenersHealthyAsync(current, ct));
        return new(native.Version, identity, true, healthy, service.Running, service.BootEnabled ?? !pending.Prepared.BootEnabledAfterSwitch);
    }

    public async Task ValidateRestoreAsync(RuntimePendingSwitch pending, CancellationToken ct)
    {
        if (pending.Snapshot!.ConfigurationIdentity is { } oldIdentity)
        {
            var current = await CurrentAsync(ct);
            var previous = await ReadConfigurationAsync(Path.Combine(Operation(pending.OperationId), "previous.yaml"), ct);
            if (Hash(previous) != oldIdentity || Hash(Encoding.UTF8.GetBytes(current.Yaml)) != oldIdentity)
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
            await ValidateNativeAsync(Binary(revision), Path.Combine(Operation(pending.OperationId), "previous.yaml"), revision.Release,
                Data(revision.RevisionId), ct);
        }
    }

    public Task StopForRecoveryAsync(RuntimePendingSwitch pending, CancellationToken ct) => host.StopAsync(ct);

    public async Task<RuntimeRestorationEvidence> RestoreAsync(RuntimePendingSwitch pending, CancellationToken ct)
    {
        var snapshot = pending.Snapshot!;
        if (snapshot.ConfigurationIdentity is { } oldIdentity) await RequireCurrentConfigurationAsync(oldIdentity, ct);
        var registration = JsonSerializer.Deserialize<MihomoServiceObservation>(await File.ReadAllBytesAsync(Path.Combine(Operation(pending.OperationId), "service.json"), ct))
            ?? throw new RuntimePolicyException("runtime.snapshot_invalid");
        if (snapshot.ConfigurationIdentity is null) { RuntimeSourceStore.RequireNoLinks(Configuration); File.Delete(Configuration); }
        else await ReplaceConfigurationAsync(await ReadConfigurationAsync(Path.Combine(Operation(pending.OperationId), "previous.yaml"), ct), ct);
        if (!registration.Exists) await host.RemoveOwnedRegistrationAsync(ct);
        else
        {
            if (pending.Previous is null) throw new RuntimePolicyException("runtime.snapshot_invalid");
            await host.ConfigureAsync(Binary(pending.Previous), Data(pending.Previous.RevisionId), Configuration,
                snapshot.ConfigurationIdentity!, snapshot.BootEnabled, ct);
            if (snapshot.Running)
            {
                await RequireCurrentConfigurationAsync(snapshot.ConfigurationIdentity!, ct);
                await host.StartAsync(ct);
            }
        }
        var actual = await host.ObserveAsync(ct);
        var native = pending.Previous is null ? null : await ValidateNativeAsync(Binary(pending.Previous), Configuration,
            pending.Previous.Release, Data(pending.Previous.RevisionId), ct);
        var hash = File.Exists(Configuration) ? Hash(await ReadConfigurationAsync(Configuration, ct)) : null;
        var current = actual.Running ? await CurrentAsync(ct) : null;
        var healthy = actual.Exists == registration.Exists && (!actual.Exists || actual.Owned && actual.BootEnabled.HasValue && pending.Previous is not null
            && SamePath(actual.Executable, Binary(pending.Previous)) && SamePath(actual.DataDirectory, Data(pending.Previous.RevisionId)))
            && (!actual.Running || actual.AppliedConfigurationIdentity == hash && await host.ListenersHealthyAsync(current!, ct));
        return new(native?.Version, hash, actual.Running, actual.BootEnabled ?? false, healthy, healthy);
    }

    public Task CleanupPreparationAsync(RuntimePendingSwitch pending, CancellationToken ct)
    {
        var path = Revision(pending.OperationId);
        if (stateStore.Read()?.Active?.RevisionId == pending.OperationId) throw new RuntimePolicyException("runtime.active_revision_cleanup_forbidden");
        DeleteOwnedRevision(path);
        DeleteDataRevision(pending.OperationId);
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

    internal async Task<MihomoPublishedRuntime> CurrentAsync(CancellationToken ct)
    {
        var grant = await authorization.ReadAsync(ct);
        if (grant.ValidUntil <= DateTimeOffset.UtcNow || grant.ValidUntil > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new RuntimePolicyException("runtime.authorization_unavailable");
        var published = grant.Published ?? throw new RuntimePolicyException("runtime.authorization_unavailable");
        MihomoRuntimeMaterial.Validate(published);
        var description = MihomoConfigurationPolicy.Validate(published);
        if (published.NetworkTransitionRequired || description.Material.NetworkTransitionRequired)
            throw new RuntimePolicyException("runtime.network_transaction_required");
        return published;
    }

    private async Task RequireCurrentConfigurationAsync(string identity, CancellationToken ct)
    {
        var current = await CurrentAsync(ct);
        if (Hash(Encoding.UTF8.GetBytes(current.Yaml)) != identity)
            throw new RuntimePolicyException("runtime.authorization_changed");
    }

    internal async Task<MihomoBuildEvidence> ValidateNativeAsync(string binary, string configuration, RuntimeRelease release, string data, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var build = MihomoNativeValidation.Parse(await MihomoOperations.VersionAsync(binary), release.OriginalVersion,
            release.Target.Os, release.Target.Architecture);
        var published = await CurrentAsync(ct);
        MihomoConfigurationPolicy.VerifyResources(MihomoConfigurationPolicy.Validate(published), data);
        RequireFeatures(build, published);
        await MihomoOperations.TestConfigurationAsync(binary, data, configuration);
        ct.ThrowIfCancellationRequested();
        return build;
    }

    internal async Task VerifyFilesAsync(RuntimeStoredRevision revision, CancellationToken ct)
    {
        foreach (var file in revision.Release.Artifact.Layout.Files)
        {
            var path = Path.Combine(Revision(revision.RevisionId), file.Path.Replace('/', Path.DirectorySeparatorChar));
            RuntimeSourceStore.RequireNoLinks(path);
            await MihomoRuntimeData.RequireRegularFileAsync(path, "runtime.installed_file_drift", ct);
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
    private string Data(Guid id) => Path.Combine(DataRoot, id.ToString("N"));
    private static void RequireFeatures(MihomoBuildEvidence build, MihomoPublishedRuntime published)
    {
        if (!published.RequiredBuildTags.All(build.BuildTags.Contains)) throw new RuntimePolicyException("runtime.native_feature_missing");
    }
    private string? RollbackDataSource(RuntimeChangePlan plan, Guid operationId)
    {
        if (plan.Request.Change != RuntimeChange.Rollback) return null;
        var revision = stateStore.Read()?.Revisions.LastOrDefault(r => r.RevisionId != operationId
            && r.Release.ReleaseId == plan.Release!.ReleaseId
            && r.Release.Artifact.Sha256.Equals(plan.Release.Artifact.Sha256, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(Data(r.RevisionId)));
        return revision is null ? throw new RuntimePolicyException("runtime.rollback_unavailable") : Data(revision.RevisionId);
    }
    private void DeleteDataRevision(Guid id)
    {
        var path = Data(id);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) != Path.GetFullPath(DataRoot))
            throw new RuntimePolicyException("runtime.staging_path_invalid");
        if (Directory.Exists(path)) DeleteTree(path);
    }
    internal string Binary(RuntimeStoredRevision revision) => Path.Combine(Revision(revision.RevisionId), revision.Release.Artifact.EntryPoint.Replace('/', Path.DirectorySeparatorChar));
    private void Protect(string path) { RuntimeSourceStore.RequireNoLinks(path); protectDirectory(path); RuntimeSourceStore.RequireNoLinks(path); }
    internal static bool SamePath(string? left, string right) => left is not null && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static async Task<byte[]> ReadConfigurationAsync(string path, CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        await MihomoRuntimeData.RequireRegularFileAsync(path, "runtime.native_configuration_invalid", ct);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 4 * 1024 * 1024) throw new RuntimePolicyException("runtime.native_configuration_invalid");
        var bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes, ct); return bytes;
    }
    internal static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var output = new FileStream(path, options); await output.WriteAsync(bytes, ct); output.Flush(true);
    }
    internal async Task ReplaceConfigurationAsync(byte[] bytes, CancellationToken ct)
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
    internal static void DeleteTree(string path)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        { RuntimeSourceStore.RequireNoLinks(child); if (Directory.Exists(child)) DeleteTree(child); else File.Delete(child); }
        Directory.Delete(path);
    }
}
