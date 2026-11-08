using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using static RelaxKonOS.PrivilegedHelper.MihomoArchiveAdapter;

namespace RelaxKonOS.PrivilegedHelper;

internal interface IMihomoConfigurationAuthorization
{
    Task<MihomoConfigurationAuthorization> ReadAsync(CancellationToken ct);
}

/// <summary>Configuration-only publication on the existing immutable program revision. The shared
/// transaction holds the instance lock. Mutable data is copied only after the real service has stopped.</summary>
internal sealed class MihomoConfigurationAdapter(string root, RuntimeArchiveStateStore states, RuntimeSourceStore sources,
    IMihomoArchiveHost host, IMihomoConfigurationAuthorization authorization, Action<string> protect,
    IRuntimeDependencyEnvironment? environment = null) : IRuntimeArchiveConfigurationAdapter
{
    private string Configuration => Path.Combine(root, "active.yaml");
    private string Operation(Guid id) => Path.Combine(root, "operations", id.ToString("N"));
    private string FileIn(RuntimePendingConfiguration pending, string name) => Path.Combine(Operation(pending.OperationId), name);
    private string Data(RuntimeStoredRevision revision) => Path.Combine(root, "data", revision.RevisionId.ToString("N"));
    private RuntimeStoredRevision Revision(RuntimePendingConfiguration pending)
    {
        var active = states.Read()?.Active;
        if (active is null || active.RevisionId != pending.Plan.ActiveRevisionId)
            throw new RuntimePolicyException("runtime.state_revision_conflict");
        return active;
    }

    // Configuration work never fetches a program, so these tools carry no download route.
    private MihomoArchiveAdapter Tools(RuntimePendingConfiguration pending, bool candidate) => new(root, states, sources,
        new RuntimeArchiveTransport(), downloadProxy: null, host, new SelectedAuthorization(authorization, pending, candidate), protect);

    public async Task PreflightAsync(RuntimeConfigurationPlan plan, RuntimeArchiveState current, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(root) || Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) == Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar))
            throw new RuntimePolicyException("runtime.staging_path_invalid");
        var pending = current.Configuration ?? throw new RuntimePolicyException("runtime.transaction_state_invalid");
        var revision = Revision(pending);
        await AuthorizeProgramAsync(pending, ct);
        var service = await host.ObserveAsync(ct);
        RequireService(service, revision);
        if (plan.NetworkIntentIdentity is not null)
        {
            if (!IsFixedLinuxHost) throw new RuntimePolicyException("runtime.network_platform_unsupported");
            if (!service.Running || service.BootEnabled != false)
                throw new RuntimePolicyException("runtime.network_service_baseline_required");
        }
        RuntimeSourceStore.RequireNoLinks(Data(revision));
        if (!Directory.Exists(Data(revision))) throw new RuntimePolicyException("runtime.data_snapshot_missing");
        await RequireMaterialAsync(pending, false, ct);
        if (plan.NetworkIntentIdentity is not null)
        {
            var (_, network) = await RequireTunMaterialAsync(pending, ct);
            if (network.DnsRoutingDomain is not null) await LinuxMihomoTunDns.RequireAvailableAsync();
        }
        else await RequireMaterialAsync(pending, true, ct);
        if (Hash(await ReadConfigurationAsync(Configuration, ct)) != plan.PreviousIdentity)
            throw new RuntimePolicyException("runtime.configuration_drift");
        var requirements = MihomoArchiveAdapter.Requirements(revision.Release)
            .Select(r => r.Kind == RuntimeDependencyKind.DiskSpace ? r with
                { MinimumBytes = checked(r.MinimumBytes!.Value + MihomoRuntimeData.MaximumBytes) } : r).ToArray();
        var report = RuntimeDependencyProbe.For(requirements, environment ?? new HostDependencyEnvironment(), root);
        if (!report.Satisfies(requirements)) throw new RuntimePolicyException("runtime.dependencies_unsatisfied");
    }

    public Task<RuntimeServiceSnapshot> CaptureAsync(Guid operationId, RuntimeArchiveState current, CancellationToken ct) =>
        Tools(current.Configuration!, false).CaptureAsync(operationId, current, ct);

    public async Task PrepareAsync(RuntimePendingConfiguration pending, CancellationToken ct)
    {
        var material = await RequireMaterialAsync(pending, true, ct);
        await WriteNewAsync(FileIn(pending, "candidate.yaml"), Encoding.UTF8.GetBytes(material.Yaml), ct);
        // Only pre-provisioned resources enter preparation; reading a running cache is not a snapshot.
        // Controlled GEO/provider resource distribution is a separate, still-required preparation layer.
        await MihomoRuntimeData.CopyAsync(Path.Combine(root, "seed-data"), FileIn(pending, "check-data"), protect, ct);
        await CheckNativeAsync(pending, true, FileIn(pending, "check-data"), ct);
    }

    public async Task ValidateSwitchAsync(RuntimePendingConfiguration pending, CancellationToken ct)
    {
        await AuthorizeProgramAsync(pending, ct);
        var observed = await host.ObserveAsync(ct);
        RequireService(observed, Revision(pending));
        if (observed.Running != pending.Snapshot!.Running || observed.BootEnabled != pending.Snapshot.BootEnabled
            || Hash(await ReadConfigurationAsync(Configuration, ct)) != pending.Plan.PreviousIdentity
            || observed.Running && observed.AppliedConfigurationIdentity != pending.Plan.PreviousIdentity)
            throw new RuntimePolicyException("runtime.state_revision_conflict");
        await CheckNativeAsync(pending, true, FileIn(pending, "check-data"), ct);
    }

    public async Task ActivateAsync(RuntimePendingConfiguration pending, CancellationToken ct)
    {
        await RequireMaterialAsync(pending, true, ct);
        await host.StopAsync(ct);
        var revision = Revision(pending);
        var observed = await host.ObserveAsync(ct);
        RequireService(observed, revision);
        if (observed.Running) throw new RuntimePolicyException("runtime.state_unknown");
        var partial = FileIn(pending, "data-backup.partial");
        var backup = FileIn(pending, "data-backup");
        await MihomoRuntimeData.CopyAsync(Data(revision), partial, protect, ct);
        RuntimeSourceStore.RequireNoLinks(backup);
        Directory.Move(partial, backup); // Presence proves that the complete bounded copy finished.
        await WriteNewAsync(FileIn(pending, "data-mutation"), pending.OperationId.ToByteArray(), ct);
        await CheckNativeAsync(pending, true, Data(revision), ct);
        await RequireMaterialAsync(pending, true, ct);
        await Tools(pending, true).ReplaceConfigurationAsync(await CandidateBytesAsync(pending, true, ct), ct);
        var binary = Tools(pending, true).Binary(revision);
        if (pending.Plan.NetworkIntentIdentity is not null)
        {
            if (!IsFixedLinuxHost) throw new RuntimePolicyException("runtime.network_platform_unsupported");
            var (_, network) = await RequireTunMaterialAsync(pending, ct);
            var intent = new MihomoTunIntent(pending.OperationId, revision.RevisionId, pending.Plan.CandidateIdentity,
                LinuxMihomoTunCheckpoint.BootId(), LinuxMihomoTunCheckpoint.NetworkNamespace(),
                network.Device, network.Address, network.Destination, network.ManagementAddress, network.ManagementUserId,
                network.ManagementHash, LinuxMihomoTunRoutes.TableFor(pending.OperationId), network.DnsRoutingDomain);
            await TunHost().ActivateTunAsync(intent, binary, Data(revision), ct);
            return;
        }
        await host.ConfigureAsync(binary, Data(revision), Configuration,
            pending.Plan.CandidateIdentity, pending.Snapshot!.BootEnabled, ct);
        if (pending.Snapshot.Running)
        {
            await RequireMaterialAsync(pending, true, ct);
            await host.StartAsync(ct);
        }
    }

    public async Task<RuntimeActivationEvidence> VerifyAsync(RuntimePendingConfiguration pending, CancellationToken ct)
    {
        var evidence = await VerifyCandidateAsync(pending, ct);
        if (evidence.MihomoTun is not null)
        {
            var (_, intent) = await RequireTunMaterialAsync(pending, ct);
            LinuxMihomoTunScope.RequirePublication(intent);
        }
        return evidence;
    }

    internal async Task<RuntimeActivationEvidence> VerifyCandidateAsync(RuntimePendingConfiguration pending, CancellationToken ct)
    {
        await AuthorizeProgramAsync(pending, ct);
        // Native checks never touch live, running mutable data.
        var build = await CheckNativeAsync(pending, true, FileIn(pending, "check-data"), ct);
        var material = await RequireMaterialAsync(pending, true, ct);
        var identity = Hash(await ReadConfigurationAsync(Configuration, ct));
        var observed = await host.ObserveAsync(ct);
        RequireService(observed, Revision(pending));
        var healthy = identity == pending.Plan.CandidateIdentity
            && (!observed.Running || observed.AppliedConfigurationIdentity == identity && await host.ListenersHealthyAsync(material, ct));
        RuntimeMihomoTunOwnership? ownership = null;
        if (pending.Plan.NetworkIntentIdentity is not null)
        {
            if (!IsFixedLinuxHost) throw new RuntimePolicyException("runtime.network_platform_unsupported");
            var installation = states.Read()!.Installation;
            ownership = await TunHost().ObserveTunOwnershipAsync(pending.OperationId, installation, ct);
        }
        return new(build.Version, identity, true, healthy, observed.Running, observed.BootEnabled!.Value, ownership);
    }

    public async Task StopForRecoveryAsync(RuntimePendingConfiguration pending, CancellationToken ct)
    {
        if (ReadTunEffect(pending) is not null)
        {
            if (pending.Plan.NetworkIntentIdentity is null || !IsFixedLinuxHost)
                throw new RuntimePolicyException("runtime.network_transaction_required");
            await TunHost().DispatchTunStopAsync(pending.OperationId, ct);
            return;
        }
        // Only a process serving one of this operation's two frozen configurations is in scope.
        var revision = Revision(pending);
        var before = await host.ObserveAsync(ct);
        RequireService(before, revision);
        if (!before.Running) return;
        if (before.AppliedConfigurationIdentity != pending.Plan.PreviousIdentity
            && before.AppliedConfigurationIdentity != pending.Plan.CandidateIdentity)
            throw new RuntimePolicyException("runtime.configuration_drift");
        await host.StopAsync(ct);
        var observed = await host.ObserveAsync(ct);
        RequireService(observed, revision);
        if (observed.Running) throw new RuntimePolicyException("runtime.recovery_stop_failed");
    }

    public async Task<bool> ObserveStoppedForRecoveryAsync(RuntimePendingConfiguration pending, CancellationToken ct)
    {
        if (ReadTunEffect(pending) is { } effect)
        {
            if (pending.Plan.NetworkIntentIdentity is null || !IsFixedLinuxHost)
                throw new RuntimePolicyException("runtime.network_transaction_required");
            if (effect.Phase == MihomoTunPhase.Restored) return true;
            if (effect.Phase is MihomoTunPhase.StopDispatched or MihomoTunPhase.RuleRemovalDispatched)
            {
                await TunHost().CompleteTunStopAsync(pending.OperationId, ct);
                return true;
            }
            if (effect.Phase is not (MihomoTunPhase.DescriptorDispatched or MihomoTunPhase.DnsDispatched or MihomoTunPhase.DnsApplied))
                throw new RuntimePolicyException("runtime.network_stop_unconfirmed");
            await RequireMaterialAsync(pending, false, ct);
            if (effect.Phase == MihomoTunPhase.DnsDispatched) await TunHost().ReconcileTunDnsAsync(pending.OperationId, ct);
            await TunHost().VerifyTunAsync(pending.OperationId, ct);
            return false;
        }
        var observed = await host.ObserveRegistrationAsync(ct);
        if (!observed.Exists || observed.BootEnabled is null) throw new RuntimePolicyException("runtime.state_unknown");
        if (observed.Running) return false;
        var stopped = await host.ObserveAsync(ct);
        RequireService(stopped, Revision(pending));
        return !stopped.Running;
    }

    public async Task ValidateRestoreAsync(RuntimePendingConfiguration pending, CancellationToken ct)
    {
        await AuthorizeProgramAsync(pending, ct);
        await RequireMaterialAsync(pending, false, ct);
        await CandidateBytesAsync(pending, false, ct);
        RuntimeSourceStore.RequireNoLinks(FileIn(pending, "data-backup"));
        RuntimeSourceStore.RequireNoLinks(FileIn(pending, "data-mutation"));
        if (File.Exists(FileIn(pending, "data-mutation")) && !Directory.Exists(FileIn(pending, "data-backup")))
            throw new RuntimePolicyException("runtime.data_snapshot_missing");
    }

    public async Task<RuntimeRestorationEvidence> RestoreAsync(RuntimePendingConfiguration pending, CancellationToken ct)
    {
        var revision = Revision(pending);
        var effect = ReadTunEffect(pending);
        if (effect is not null)
        {
            if (!IsFixedLinuxHost) throw new RuntimePolicyException("runtime.network_transaction_required");
            await VerifyRestoredNetworkAsync(pending, effect, ct);
            var registration = await host.ObserveRegistrationAsync(ct);
            if (registration.Running) return await ObserveRestoredServiceAsync(pending, effect, ct);
        }
        var backup = FileIn(pending, "data-backup");
        if (Directory.Exists(backup))
        {
            // Keep the immutable backup throughout every retry. Prepare a complete replacement before
            // removing failed live data; a crash between delete/move is recoverable from this same backup.
            var replacement = FileIn(pending, "data-restore");
            Discard(replacement);
            await MihomoRuntimeData.CopyAsync(backup, replacement, protect, ct);
            Discard(Data(revision));
            Directory.Move(replacement, Data(revision));
        }
        await RequireMaterialAsync(pending, false, ct);
        var bytes = await CandidateBytesAsync(pending, false, ct);
        await Tools(pending, false).ReplaceConfigurationAsync(bytes, ct);
        var build = await CheckNativeAsync(pending, false, Data(revision), ct);
        await host.ConfigureAsync(Tools(pending, false).Binary(revision), Data(revision), Configuration,
            pending.Plan.PreviousIdentity, pending.Snapshot!.BootEnabled, ct);
        if (pending.Snapshot.Running)
        {
            await RequireMaterialAsync(pending, false, ct);
            try { await host.StartAsync(ct); }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException)
            {
                // systemd may have started the restored program before its command failed to return.
                // The full process, credential and listener verification below still decides the result.
                var started = await host.ObserveAsync(ct);
                RequireService(started, revision);
                if (!started.Running || started.AppliedConfigurationIdentity != pending.Plan.PreviousIdentity) throw;
            }
        }
        var material = await RequireMaterialAsync(pending, false, ct);
        var observed = await host.ObserveAsync(ct);
        RequireService(observed, revision);
        var identity = Hash(await ReadConfigurationAsync(Configuration, ct));
        var healthy = identity == pending.Plan.PreviousIdentity && (!observed.Running
            || observed.AppliedConfigurationIdentity == identity && await host.ListenersHealthyAsync(material, ct));
        return new(build.Version, identity, observed.Running, observed.BootEnabled!.Value, true, healthy)
        {
            RestoredNetworkCheckpoint = pending.Plan.NetworkIntentIdentity is not null ? effect is not null : null
        };
    }

    public async Task<RuntimeRestorationEvidence> VerifyRestoredAsync(RuntimePendingConfiguration pending, CancellationToken ct)
    {
        if (!IsFixedLinuxHost || pending.Plan.NetworkIntentIdentity is null)
            throw new RuntimePolicyException("runtime.network_transaction_required");
        var effect = ReadTunEffect(pending);
        if (pending.RestoredNetworkCheckpoint != (effect is not null))
            throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
        if (effect is not null) await VerifyRestoredNetworkAsync(pending, effect, ct);
        return await ObserveRestoredServiceAsync(pending, effect, ct);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static async Task VerifyRestoredNetworkAsync(RuntimePendingConfiguration pending, MihomoTunEffect effect, CancellationToken ct)
    {
        var identity = MihomoRuntimeMaterial.ConfigurationTunIntentIdentity(pending.OperationId, pending.Plan,
            LinuxMihomoArchiveHost.TunNetwork(effect.Intent));
        if (identity != pending.Plan.NetworkIntentIdentity) throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
        await LinuxMihomoArchiveHost.VerifyRestoredTunNetworkAsync(effect, ct);
    }

    private async Task<RuntimeRestorationEvidence> ObserveRestoredServiceAsync(RuntimePendingConfiguration pending, MihomoTunEffect? effect,
        CancellationToken ct)
    {
        await AuthorizeProgramAsync(pending, ct);
        var revision = Revision(pending);
        var identity = Hash(await ReadConfigurationAsync(Configuration, ct));
        if (identity != pending.Plan.PreviousIdentity) throw new RuntimePolicyException("runtime.configuration_drift");
        var observed = await host.ObserveAsync(ct);
        RequireService(observed, revision);
        if (observed.TunOperation is not null || observed.Running && observed.AppliedConfigurationIdentity != identity)
            throw new RuntimePolicyException("runtime.configuration_drift");
        var previous = await RequireMaterialAsync(pending, false, ct);
        return new(revision.MeasuredVersion, identity, observed.Running, observed.BootEnabled!.Value,
            true, !observed.Running || await host.ListenersHealthyAsync(previous, ct))
        {
            RestoredNetworkCheckpoint = effect is not null
        };
    }

    public Task CleanupPreparationAsync(RuntimePendingConfiguration pending, CancellationToken ct)
    {
        Discard(FileIn(pending, "check-data"));
        Discard(FileIn(pending, "data-backup.partial"));
        Discard(FileIn(pending, "data-restore"));
        // previous.yaml, service.json and data-backup are retained by the shared receipt.
        return Task.CompletedTask;
    }

    private async Task AuthorizeProgramAsync(RuntimePendingConfiguration pending, CancellationToken ct)
    {
        var revision = Revision(pending);
        RequireTrustedRevision(sources, revision);
        await Tools(pending, false).VerifyFilesAsync(revision, ct);
    }

    private async Task<MihomoPublishedRuntime> RequireMaterialAsync(RuntimePendingConfiguration pending, bool candidate, CancellationToken ct)
    {
        if (candidate && pending.Plan.NetworkIntentIdentity is not null)
            return (await RequireTunMaterialAsync(pending, ct)).Material;
        var material = await Tools(pending, candidate).CurrentAsync(ct);
        if (Hash(Encoding.UTF8.GetBytes(material.Yaml)) != (candidate ? pending.Plan.CandidateIdentity : pending.Plan.PreviousIdentity))
            throw new RuntimePolicyException("runtime.authorization_changed");
        return material;
    }

    private async Task<(MihomoPublishedRuntime Material, MihomoTunNetworkIntent Network)> RequireTunMaterialAsync(
        RuntimePendingConfiguration pending, CancellationToken ct)
    {
        var grant = await authorization.ReadAsync(ct);
        var now = DateTimeOffset.UtcNow;
        if (grant.OperationId != pending.OperationId || grant.ValidUntil <= now || grant.ValidUntil > now.AddMinutes(5)
            || grant.Candidate is not { } material || grant.TunIntent is not { } network)
            throw new RuntimePolicyException("runtime.authorization_unavailable");
        MihomoRuntimeMaterial.RequireConfigurationTunIntent(pending.OperationId, pending.Plan, material, network);
        if (!OperatingSystem.IsLinux()) throw new RuntimePolicyException("runtime.network_platform_unsupported");
        if (!LinuxMihomoTunScope.Read().Matches(network))
            throw new RuntimePolicyException("runtime.network_scope_changed");
        return (material, network);
    }

    [System.Runtime.Versioning.SupportedOSPlatformGuard("linux")]
    private bool IsFixedLinuxHost => OperatingSystem.IsLinux() && host is LinuxMihomoArchiveHost && root == LinuxMihomoArchiveHost.Root;

    private LinuxMihomoArchiveHost TunHost() => (LinuxMihomoArchiveHost)host;

    private MihomoTunEffect? ReadTunEffect(RuntimePendingConfiguration pending) => OperatingSystem.IsLinux()
        ? new LinuxMihomoTunCheckpoint(root, pending.OperationId, pending.Plan.ActiveRevisionId, pending.Plan.CandidateIdentity).Read()
        : null;

    private async Task<byte[]> CandidateBytesAsync(RuntimePendingConfiguration pending, bool candidate, CancellationToken ct)
    {
        var bytes = await ReadConfigurationAsync(FileIn(pending, candidate ? "candidate.yaml" : "previous.yaml"), ct);
        if (Hash(bytes) != (candidate ? pending.Plan.CandidateIdentity : pending.Plan.PreviousIdentity))
            throw new RuntimePolicyException("runtime.configuration_drift");
        return bytes;
    }

    private async Task<MihomoBuildEvidence> CheckNativeAsync(RuntimePendingConfiguration pending, bool candidate, string data, CancellationToken ct)
    {
        var material = await RequireMaterialAsync(pending, candidate, ct);
        await CandidateBytesAsync(pending, candidate, ct);
        var revision = Revision(pending);
        var build = await MihomoArchiveAdapter.ValidateNativeAsync(Tools(pending, candidate).Binary(revision),
            FileIn(pending, candidate ? "candidate.yaml" : "previous.yaml"), revision.Release, data, material, ct);
        await CandidateBytesAsync(pending, candidate, ct);
        await RequireMaterialAsync(pending, candidate, ct);
        return build;
    }

    private void RequireService(MihomoServiceObservation observed, RuntimeStoredRevision revision)
    {
        if (!observed.Exists || !observed.Owned || observed.BootEnabled is null
            || !SamePath(observed.Executable, Path.Combine(root, "versions", revision.RevisionId.ToString("N"), revision.Release.Artifact.EntryPoint))
            || !SamePath(observed.DataDirectory, Data(revision))) throw new RuntimePolicyException("runtime.state_unknown");
    }

    private static void Discard(string path)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        if (Directory.Exists(path)) DeleteTree(path);
    }

    private sealed class SelectedAuthorization(IMihomoConfigurationAuthorization authorization, RuntimePendingConfiguration pending,
        bool candidate) : IMihomoArchiveAuthorization
    {
        public async Task<MihomoCurrentAuthorization> ReadAsync(CancellationToken ct)
        {
            var grant = await authorization.ReadAsync(ct);
            if (grant.OperationId != pending.OperationId) throw new RuntimePolicyException("runtime.authorization_unavailable");
            if (candidate && grant.Candidate is not null)
                MihomoRuntimeMaterial.RequireConfigurationTunIntent(pending.OperationId, pending.Plan, grant.Candidate, grant.TunIntent);
            return new(grant.ValidUntil, candidate ? grant.Candidate : grant.Previous);
        }
    }
}
