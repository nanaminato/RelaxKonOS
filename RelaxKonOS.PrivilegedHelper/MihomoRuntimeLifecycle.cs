using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Production composition for the one Helper-owned mihomo installation.</summary>
internal sealed class MihomoRuntimeLifecycle(string root, RuntimeArchiveStateStore states, RuntimeSourceStore sources,
    IMihomoArchiveHost host, Action<string> protect, IRuntimeDependencyEnvironment? environment = null,
    IRuntimeArchiveTransport? transport = null, string? resourceDirectory = null,
    IReadOnlyList<MihomoGeoDataFile>? resourceFiles = null)
{
    // Requirements are measured by the Host, never declared by the catalog publisher.
    private readonly IRuntimeDependencyEnvironment environment = environment ?? new HostDependencyEnvironment();

    internal static MihomoRuntimeLifecycle Create()
    {
        if (!OperatingSystem.IsLinux()) throw new RuntimePolicyException("runtime.platform_unsupported");
        return new(LinuxMihomoArchiveHost.Root,
            new(Path.Combine(LinuxMihomoArchiveHost.Root, "state"), RuntimeSourceProtection.Protect),
            RuntimeSourceProtection.Store(), new LinuxMihomoArchiveHost(), RuntimeSourceProtection.Protect);
    }

    internal async Task<RuntimeInstallationSnapshot> QueryAsync(CancellationToken ct, Guid? receiptOperationId = null)
    {
        var state = await EnsureStateAsync(ct);
        var receipt = state.Receipts.FirstOrDefault(r => r.OperationId == receiptOperationId);
        var observation = HasPending(state) ? (state.Installation, (string?)null)
            : await ObserveInstallationAsync(state, ct);
        var verified = observation.Item1.Ownership == RuntimeOwnership.Managed;
        return new(observation.Item1, verified ? state.Active?.MeasuredVersion : null, verified ? state.Active?.RevisionId : null, state.ConfigurationIdentity,
            state.Pending?.OperationId ?? state.Removal?.OperationId ?? state.Configuration?.OperationId,
            state.Pending?.Stage ?? state.Removal?.Stage ?? state.Configuration?.Stage,
            state.Pending?.RecoveryProblemCode ?? state.Pending?.ProblemCode
                ?? state.Removal?.RecoveryProblemCode ?? state.Removal?.ProblemCode
                ?? state.Configuration?.RecoveryProblemCode ?? state.Configuration?.ProblemCode ?? observation.Item2,
            receipt is null ? null : new(receipt.OperationId, receipt.Outcome, receipt.ProblemCode),
            state.Revisions.Select(r => new RuntimeInstalledRelease(r.RevisionId, r.Release.ReleaseId, r.MeasuredVersion)).ToArray(),
            // Read-only environment facts, so a refusal can be explained before anyone presses install.
            RuntimeDependencyProbe.Environment(environment, root), states.ReadCommittedWitness(state, receipt));
    }

    internal async Task<RuntimePlanResult> PlanAsync(string sourceId, RuntimePlanRequest request, CancellationToken ct)
    {
        await EnsureStateAsync(ct);
        using var session = states.Acquire();
        var state = session.State!;
        if (HasPending(state)) return new(null, ["runtime.recovery_required"]);
        var observation = await ObserveInstallationAsync(state, ct);
        // Non-managed ownership can change outside RelaxKonOS. Reconcile only proven service absence or
        // external ownership under the instance lock, never adopt files or a service as managed. Advancing
        // the revision invalidates any plan made against the previous observation.
        if (state.Active is null && observation.Installation.Ownership is RuntimeOwnership.Absent or RuntimeOwnership.External
            && state.Installation.Ownership != observation.Installation.Ownership)
        {
            session.Save(state with { Installation = observation.Installation with
                { StateRevision = checked(state.Installation.StateRevision + 1) } });
            state = session.State!;
        }
        if (observation.Problem is { } problem) return new(null, [problem]);
        return Plan(sourceId, request, state);
    }

    private static bool HasPending(RuntimeArchiveState state) => state.Pending is not null || state.Removal is not null
        || state.Configuration is not null || state.Installation.RecoveryRequired;

    private async Task<(RuntimeInstallation Installation, string? Problem)> ObserveInstallationAsync(RuntimeArchiveState state, CancellationToken ct)
    {
        try
        {
            var service = await host.ObserveAsync(ct);
            if (state.Active is null)
            {
                if (service.Exists) return (state.Installation with { Ownership = RuntimeOwnership.External }, "runtime.external_installation");
                if (state.ConfigurationIdentity is { } retained && Convert.ToHexString(SHA256.HashData(
                    await MihomoArchiveAdapter.ReadConfigurationAsync(Path.Combine(root, "active.yaml"), ct))) != retained)
                    throw new RuntimePolicyException("runtime.configuration_drift");
                // Retained published configuration is owned data, not an unknown flat installation.
                if (state.ConfigurationIdentity is null && (File.Exists(Path.Combine(root, "active.yaml"))
                    || File.Exists(Path.Combine(root, "mihomo")) || File.Exists(Path.Combine(root, "installed.json"))))
                    return (state.Installation with { Ownership = RuntimeOwnership.Unknown }, "runtime.state_unknown");
                return (state.Installation with { Ownership = RuntimeOwnership.Absent }, null);
            }
            var active = state.Active;
            var binary = Path.Combine(root, "versions", active.RevisionId.ToString("N"), active.Release.Artifact.EntryPoint);
            var data = Path.Combine(root, "data", active.RevisionId.ToString("N"));
            if (!service.Owned) return (state.Installation with { Ownership = RuntimeOwnership.External }, "runtime.external_installation");
            if (!service.Exists || service.BootEnabled is null || !MihomoArchiveAdapter.SamePath(service.Executable, binary)
                || !MihomoArchiveAdapter.SamePath(service.DataDirectory, data))
                return (state.Installation with { Ownership = RuntimeOwnership.Unknown }, "runtime.state_unknown");
            RuntimeSourceStore.RequireNoLinks(data);
            if (!Directory.Exists(data)) throw new RuntimePolicyException("runtime.data_snapshot_missing");
            foreach (var file in active.Release.Artifact.Layout.Files)
            {
                var path = Path.Combine(root, "versions", active.RevisionId.ToString("N"), file.Path);
                RuntimeSourceStore.RequireNoLinks(path);
                await MihomoRuntimeData.RequireRegularFileAsync(path, "runtime.installed_file_drift", ct);
                if (OperatingSystem.IsLinux() && file.Executable && (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) == 0)
                    throw new RuntimePolicyException("runtime.installed_file_drift");
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length != file.Length || !Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new RuntimePolicyException("runtime.installed_file_drift");
            }
            var identity = MihomoArchiveAdapter.Hash(await MihomoArchiveAdapter.ReadConfigurationAsync(Path.Combine(root, "active.yaml"), ct));
            if (identity != state.ConfigurationIdentity || service.Running && service.AppliedConfigurationIdentity != identity)
                throw new RuntimePolicyException("runtime.configuration_drift");
            return (state.Installation, null);
        }
        catch (RuntimePolicyException ex)
        {
            return (state.Installation with { Ownership = ex.ProblemCode == "runtime.external_installation" ? RuntimeOwnership.External : RuntimeOwnership.Unknown }, ex.ProblemCode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return (state.Installation with { Ownership = RuntimeOwnership.Unknown }, "runtime.state_unknown"); }
    }

    private RuntimePlanResult Plan(string sourceId, RuntimePlanRequest request, RuntimeArchiveState state)
    {
        // Removal must stay possible when the source has expired or been revoked: it needs no release.
        if (request.Change == RuntimeChange.Uninstall)
            return RuntimePlanning.Create(request, state.Installation,
                new(sourceId, string.Empty, false, RuntimeTrust.Unconfirmed, 0),
                new(sourceId, 0, 0, DateTimeOffset.MaxValue, true, []), 0, DateTimeOffset.UtcNow, dependencies: null);
        var source = sources.Read(sourceId) ?? throw new RuntimePolicyException("runtime.source_not_found");
        if (source.Source.RuntimeId != "mihomo") throw new RuntimePolicyException("runtime.source_invalid");
        var catalog = source.Cached?.Catalog ?? throw new RuntimePolicyException("runtime.catalog_missing");
        // Measure the requested release itself; an unresolved release id must not silently skip the probe.
        var requested = catalog.Releases.SingleOrDefault(r => r.ReleaseId == request.ReleaseId);
        var dependencies = requested is null ? null
            : RuntimeDependencyProbe.For(MihomoArchiveAdapter.Requirements(requested), environment, root);
        var result = RuntimePlanning.Create(request, state.Installation,
            new(source.Source.SourceId, source.Source.Publisher, source.Source.Enabled, RuntimeTrust.Confirmed, source.Source.TrustRevision),
            catalog, source.History.HighWater, DateTimeOffset.UtcNow, dependencies);
        if (result.Plan?.Release is { } release && request.Change == RuntimeChange.Update && state.Active is { } current
            && MihomoNativeValidation.UpdateProblem(current.MeasuredVersion, release.OriginalVersion) is { } problem)
            return new(null, [problem], dependencies);
        return result;
    }

    internal async Task<RuntimeSwitchResult> ExecuteAsync(Guid operationId, RuntimeChangePlan plan,
        RuntimeAuthorizationChannel channel, CancellationToken ct, RuntimeStageReporter? progress = null,
        RuntimeDownloadProxy? downloadProxy = null)
    {
        // The channel descriptor never enters a frozen plan, durable journal, or idempotency hash.
        if (channel.OperationId != operationId || channel.RuntimeId != "mihomo")
            throw new RuntimePolicyException("runtime.authorization_unavailable");
        await EnsureStateAsync(ct);
        // A cancellation request arrives through a second Helper process and is carried into preparation by
        // this token. The transaction itself decides when it is too late to honour it.
        using var cancellation = RuntimeCancellation.Watch(root, operationId, ct);
        // Removal never reads published credentials, so it does not depend on a live authorization channel.
        return plan.Request.Change == RuntimeChange.Uninstall
            ? await Removal(channel, progress).ExecuteAsync(operationId, plan, cancellation.Token)
            : await Switch(channel, progress, plan.Request.Change == RuntimeChange.Install ? plan : null, downloadProxy)
                .ExecuteAsync(operationId, plan, cancellation.Token);
    }

    /// <summary>
    /// Records a cancellation request for one transaction. It reads no state and takes no instance lock, so it
    /// still works while the transaction owns both; the operation's own receipt stays the only authority on
    /// whether the request was early enough to change the outcome.
    /// </summary>
    internal RuntimeCancelRequestResult CancelRequest(Guid operationId)
    {
        if (operationId == Guid.Empty) throw new RuntimePolicyException("runtime.operation_id_invalid");
        RuntimeCancelChannel.Request(root, operationId);
        return new(operationId, true);
    }

    internal async Task<RuntimeSwitchResult> ConfigureAsync(Guid operationId, RuntimeConfigurationPlan plan,
        RuntimeAuthorizationChannel channel, CancellationToken ct, RuntimeStageReporter? progress = null)
    {
        if (channel.OperationId != operationId || channel.RuntimeId != "mihomo")
            throw new RuntimePolicyException("runtime.authorization_unavailable");
        await EnsureStateAsync(ct);
        using var cancellation = RuntimeCancellation.Watch(root, operationId, ct);
        return await Configuration(channel, progress).ExecuteAsync(operationId, plan, cancellation.Token);
    }

    internal async Task<RuntimeSwitchResult?> RecoverAsync(RuntimeAuthorizationChannel channel, Guid operationId, CancellationToken ct,
        RuntimeStageReporter? progress = null)
    {
        if (channel.RuntimeId != "mihomo") throw new RuntimePolicyException("runtime.authorization_unavailable");
        await EnsureStateAsync(ct);
        if (states.Read()?.Configuration is not null)
            return await Configuration(channel, progress).RecoverAsync(operationId, ct);
        return states.Read()?.Removal is not null
            ? await Removal(channel, progress).RecoverAsync(operationId, ct)
            : await Switch(channel, progress, states.Read()?.Pending is { Plan.Request.Change: RuntimeChange.Install } installation
                ? installation.Plan : null, downloadProxy: null).RecoverAsync(operationId, ct);
    }

    /// <summary>
    /// Ledger facts plus the fixed host's live observation. A failed observation is reported as a problem
    /// code rather than guessed: "unknown" must never be rendered as "stopped" or "not installed".
    /// </summary>
    internal async Task<RuntimeRecoverySnapshot> RecoveryAsync(CancellationToken ct)
    {
        var snapshot = RuntimeArchiveRecovery.Describe(await EnsureStateAsync(ct));
        if (!snapshot.Required) return snapshot;
        try
        {
            var observed = await host.ObserveAsync(ct);
            return snapshot with { Service = new(observed.Exists, observed.Running, observed.BootEnabled) };
        }
        catch (RuntimePolicyException ex)
        {
            return await RecoveryObservationFailureAsync(snapshot, ex.ProblemCode, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        { return await RecoveryObservationFailureAsync(snapshot, "runtime.service_observation_failed", ct); }
    }

    private async Task<RuntimeRecoverySnapshot> RecoveryObservationFailureAsync(RuntimeRecoverySnapshot snapshot, string problem, CancellationToken ct)
    {
        try { return snapshot with { Service = await host.ObserveRegistrationAsync(ct), ServiceProblem = problem }; }
        catch (Exception observation) when (observation is RuntimePolicyException or IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception) { return snapshot with { ServiceProblem = problem }; }
    }

    /// <summary>
    /// Emergency stop of the managed service only. It reads no published credential, so it keeps working after
    /// they are revoked, and it never touches the transaction ledger: the operation stays unresolved.
    /// </summary>
    internal async Task<RuntimeRecoverySnapshot> StopAsync(Guid operationId, CancellationToken ct)
    {
        var snapshot = RuntimeArchiveRecovery.Require(RuntimeArchiveRecovery.Describe(await EnsureStateAsync(ct)), operationId);
        // A unit this installation does not own must not be stopped by its emergency entry.
        var observed = await host.ObserveRegistrationAsync(ct);
        if (observed.Exists)
        {
            await host.StopAsync(CancellationToken.None);
            try { await host.SetBootAsync(false, CancellationToken.None); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.state_unknown") { }
            observed = await host.ObserveRegistrationAsync(ct);
        }
        if (observed.Running) throw new RuntimePolicyException("runtime.recovery_stop_failed");
        return snapshot with { Service = observed };
    }

    /// <summary>Closes an unrecoverable transaction. The stop is re-observed here instead of trusted from an earlier call.</summary>
    internal async Task<RuntimeSwitchResult> CloseAsync(Guid operationId, CancellationToken ct)
    {
        await EnsureStateAsync(ct);
        var observed = await host.ObserveRegistrationAsync(ct);
        using var session = states.Acquire();
        return RuntimeArchiveRecovery.Close(session, operationId, confirmed: true,
            new(observed.Exists, observed.Running, observed.BootEnabled));
    }

    internal async Task<RuntimeResourceSnapshot> QueryResourcesAsync(CancellationToken ct)
    {
        await EnsureStateAsync(ct);
        using var session = states.Acquire();
        var state = session.State ?? throw new RuntimePolicyException("runtime.state_unknown");
        if (HasPending(state)) return new(state.Active?.RevisionId, [], "runtime.recovery_required");
        var observation = await ObserveInstallationAsync(state, ct);
        if (observation.Problem is { } problem) return new(null, [], problem);
        if (state.Active is not { } active) return new(null, [], "runtime.installation_absent");
        var directory = Path.Combine(root, "data", active.RevisionId.ToString("N"));
        var files = new List<RuntimeResourceObservation>();
        foreach (var file in resourceFiles ?? MihomoGeoDataFiles.Bundled)
        {
            var path = Path.Combine(directory, file.Name);
            try
            {
                RuntimeSourceStore.RequireNoLinks(path);
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                    throw new RuntimePolicyException("runtime.resource_digest_mismatch");
                var measured = await MeasureAsync(path, file, ct);
                files.Add(new(file.Name, RuntimeResourceState.Verified, measured.Length, measured.Digest));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { files.Add(new(file.Name, RuntimeResourceState.Missing)); }
            catch (RuntimePolicyException ex)
            {
                files.Add(new(file.Name, ex.ProblemCode == "runtime.resource_digest_mismatch"
                    ? RuntimeResourceState.Invalid : RuntimeResourceState.Unknown, ProblemCode: ex.ProblemCode));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { files.Add(new(file.Name, RuntimeResourceState.Unknown, ProblemCode: "runtime.resource_observation_failed")); }
        }
        return new(active.RevisionId, files);
    }

    /// <summary>
    /// Places the declared GEO resource set into the pre-provisioned data baseline and the active revision.
    /// The bytes come from the package installed beside this Helper and are accepted only when they match the
    /// digests compiled into the Helper: nothing is downloaded, the set is closed, and no client-supplied path
    /// or URL can extend it. Provider, certificate, and key resources remain outside this operation.
    /// </summary>
    internal async Task<RuntimeResourceReport> PrepareResourcesAsync(CancellationToken ct)
    {
        await EnsureStateAsync(ct);
        using var session = states.Acquire();
        var state = session.State ?? throw new RuntimePolicyException("runtime.state_unknown");
        if (HasPending(state)) throw new RuntimePolicyException("runtime.recovery_required");
        var observation = await ObserveInstallationAsync(state, ct);
        if (observation.Problem is { } problem) throw new RuntimePolicyException(problem);
        if (state.Active is not { } active) throw new RuntimePolicyException("runtime.installation_absent");
        var data = Path.Combine(root, "data", active.RevisionId.ToString("N"));
        var seed = Path.Combine(root, "seed-data");
        RuntimeSourceStore.RequireNoLinks(data);
        if (!Directory.Exists(data)) throw new RuntimePolicyException("runtime.data_snapshot_missing");
        var directory = Path.GetFullPath(resourceDirectory ?? BundledResourceDirectory);
        if (!Directory.Exists(directory)) throw new RuntimePolicyException("runtime.resource_source_missing");
        var placed = new List<RuntimeResourceFile>();
        foreach (var file in resourceFiles ?? MihomoGeoDataFiles.Bundled)
        {
            var from = Path.Combine(directory, file.Name);
            var (length, digest) = await MeasureAsync(from, file, ct);
            // The baseline feeds every later revision; the active revision feeds the running service.
            foreach (var target in new[] { seed, data }) await PlaceAsync(from, Path.Combine(target, file.Name), digest, ct);
            placed.Add(new(file.Name, length, digest));
        }
        // The baseline is fully Helper-owned. Live data may hold process-written cache files, so only the
        // files placed here are mode-restricted rather than re-verifying the whole directory.
        protect(seed);
        return new(active.RevisionId, placed);
    }

    // The installed Helper is an independent component, not a sibling of the Server directory.
    // Only its own versioned payload supplies resources; no alternative install-root lookup.
    internal static string BundledResourceDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "Mihomo", "GeoData");

    /// <summary>The declared digest is the only accepted content, so a replaced package asset fails here.</summary>
    private static async Task<(long Length, string Digest)> MeasureAsync(string path, MihomoGeoDataFile file, CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new RuntimePolicyException("runtime.resource_source_missing");
        if (info.Length is <= 0 or > MihomoGeoDataFiles.MaximumBytes)
            throw new RuntimePolicyException("runtime.resource_digest_mismatch");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
        if (!MihomoGeoDataFiles.DigestMatches(file.Sha256, digest))
            throw new RuntimePolicyException("runtime.resource_digest_mismatch");
        return (info.Length, digest);
    }

    /// <summary>Resources are added or replaced whole, so a running service never observes a partial database.</summary>
    private static async Task PlaceAsync(string from, string destination, string digest, CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(destination);
        if (File.Exists(destination))
        {
            await using var existing = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            if (MihomoGeoDataFiles.DigestMatches(digest, Convert.ToHexString(await SHA256.HashDataAsync(existing, ct)))) return;
        }
        var directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".resource");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var output = new FileStream(temporary, options))
            {
                await input.CopyToAsync(output, ct);
                await output.FlushAsync(ct);
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            RuntimeSourceStore.RequireNoLinks(destination);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private RuntimeArchiveRemoval Removal(RuntimeAuthorizationChannel channel, RuntimeStageReporter? progress) =>
        new(states, new MihomoRemovalAdapter(root, host, new Authorization(channel), sources, protect), progress);

    private RuntimeArchiveConfiguration Configuration(RuntimeAuthorizationChannel channel, RuntimeStageReporter? progress) =>
        new(states, new MihomoConfigurationAdapter(root, states, sources, host, new ConfigurationAuthorization(channel), protect, environment), progress);

    private sealed class ConfigurationAuthorization(RuntimeAuthorizationChannel channel) : IMihomoConfigurationAuthorization
    {
        public Task<MihomoConfigurationAuthorization> ReadAsync(CancellationToken ct) =>
            RuntimeAuthorizationClient.ReadAsync<MihomoConfigurationAuthorization>(channel, channel.OperationId, ct);
    }

    private RuntimeArchiveSwitch Switch(RuntimeAuthorizationChannel channel, RuntimeStageReporter? progress,
        RuntimeChangePlan? installationPlan, RuntimeDownloadProxy? downloadProxy) => new(states,
        new MihomoArchiveAdapter(root, states, sources, transport ?? new RuntimeArchiveTransport(), downloadProxy, host,
            installationPlan is null ? new Authorization(channel) : new MihomoInstallationAuthorizationReader(states, installationPlan,
                ct => RuntimeAuthorizationClient.ReadAsync<MihomoInstallationAuthorization>(channel, channel.OperationId, ct)), protect, progress, environment), progress);

    private async Task<RuntimeArchiveState> EnsureStateAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || MihomoNativeValidation.ArchitectureName.Length == 0) throw new RuntimePolicyException("runtime.platform_unsupported");
        var target = new RuntimeTarget(MihomoNativeValidation.PlatformName, MihomoNativeValidation.ArchitectureName, null, null,
            RuntimeInstallMode.Archive, OperatingSystem.IsWindows() ? "scm" : "systemd");
        var hostId = HostIdentity();
        var existing = states.Read();
        if (existing is not null)
        {
            if (existing.Installation.HostId != hostId || existing.Installation.RuntimeId != "mihomo" || existing.Installation.Target != target)
                throw new RuntimePolicyException("runtime.installation_mismatch");
            return existing;
        }
        protect(root);
        var ownership = RuntimeOwnership.Absent;
        try { if ((await host.ObserveAsync(ct)).Exists) ownership = RuntimeOwnership.External; }
        catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.external_installation") { ownership = RuntimeOwnership.External; }
        if (ownership == RuntimeOwnership.Absent && (File.Exists(Path.Combine(root, "active.yaml"))
            || File.Exists(Path.Combine(root, OperatingSystem.IsWindows() ? "mihomo.exe" : "mihomo"))
            || File.Exists(Path.Combine(root, "installed.json")))) ownership = RuntimeOwnership.Unknown;
        return states.Initialize(new(Guid.NewGuid(), hostId, "mihomo", 0, ownership, target, null, false), null);
    }

    private static string HostIdentity()
    {
        string identity;
        if (OperatingSystem.IsLinux()) identity = File.ReadAllText("/etc/machine-id").Trim();
        else if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            identity = key?.GetValue("MachineGuid") as string ?? throw new RuntimePolicyException("runtime.state_unknown");
        }
        else throw new RuntimePolicyException("runtime.platform_unsupported");
        if (OperatingSystem.IsLinux() ? identity.Length != 32 || !identity.All(Uri.IsHexDigit) : !Guid.TryParse(identity, out _))
            throw new RuntimePolicyException("runtime.state_unknown");
        return "host-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private sealed class Authorization(RuntimeAuthorizationChannel channel) : IMihomoArchiveAuthorization
    {
        public Task<MihomoCurrentAuthorization> ReadAsync(CancellationToken ct) => RuntimeAuthorizationClient.ReadAsync<MihomoCurrentAuthorization>(channel, channel.OperationId, ct);
    }
}
