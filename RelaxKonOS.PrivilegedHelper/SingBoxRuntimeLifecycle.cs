using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Production composition for the one Helper-owned sing-box installation.</summary>
internal sealed class SingBoxRuntimeLifecycle(string root, RuntimeArchiveStateStore states, RuntimeSourceStore sources,
    ISingBoxArchiveHost host, Action<string> protect, IRuntimeDependencyEnvironment? environment = null)
{
    // Requirements are measured by the Host, never declared by the catalog publisher.
    private readonly IRuntimeDependencyEnvironment environment = environment ?? new HostDependencyEnvironment();

    internal static SingBoxRuntimeLifecycle Create() => new(SingBoxOperations.Root,
        new(Path.Combine(SingBoxOperations.Root, "state"), RuntimeSourceProtection.Protect),
        RuntimeSourceProtection.Store(), SingBoxOperations.CreateArchiveHost(), RuntimeSourceProtection.Protect);

    internal async Task<RuntimeInstallationSnapshot> QueryAsync(CancellationToken ct, Guid? receiptOperationId = null)
    {
        var state = await EnsureStateAsync(ct);
        var receipt = state.Receipts.FirstOrDefault(r => r.OperationId == receiptOperationId);
        return new(state.Installation, state.Active?.MeasuredVersion, state.Active?.RevisionId, state.ConfigurationIdentity,
            state.Pending?.OperationId ?? state.Removal?.OperationId,
            state.Pending?.Stage ?? state.Removal?.Stage,
            state.Pending?.RecoveryProblemCode ?? state.Pending?.ProblemCode
                ?? state.Removal?.RecoveryProblemCode ?? state.Removal?.ProblemCode,
            receipt is null ? null : new(receipt.OperationId, receipt.Outcome, receipt.ProblemCode),
            state.Revisions.Select(r => new RuntimeInstalledRelease(r.RevisionId, r.Release.ReleaseId, r.MeasuredVersion)).ToArray(),
            // Read-only environment facts, so a refusal can be explained before anyone presses install.
            RuntimeDependencyProbe.Environment(environment, root));
    }

    internal async Task<RuntimePlanResult> PlanAsync(string sourceId, RuntimePlanRequest request, CancellationToken ct)
    {
        await EnsureStateAsync(ct);
        using var session = states.Acquire();
        return Plan(sourceId, request, session.State!);
    }

    private RuntimePlanResult Plan(string sourceId, RuntimePlanRequest request, RuntimeArchiveState state)
    {
        // Removal must stay possible when the source has expired or been revoked: it needs no release.
        if (request.Change == RuntimeChange.Uninstall)
            return RuntimePlanning.Create(request, state.Installation,
                new(sourceId, string.Empty, false, RuntimeTrust.Unconfirmed, 0),
                new(sourceId, 0, 0, DateTimeOffset.MaxValue, true, []), 0, DateTimeOffset.UtcNow, dependencies: null);
        var source = sources.Read(sourceId) ?? throw new RuntimePolicyException("runtime.source_not_found");
        if (source.Source.RuntimeId != "sing-box") throw new RuntimePolicyException("runtime.source_invalid");
        var catalog = source.Cached?.Catalog ?? throw new RuntimePolicyException("runtime.catalog_missing");
        // Measure the requested release itself; an unresolved release id must not silently skip the probe.
        var requested = catalog.Releases.SingleOrDefault(r => r.ReleaseId == request.ReleaseId);
        var dependencies = requested is null ? null
            : RuntimeDependencyProbe.For(RuntimeArchiveDependencies.For(requested), environment, root);
        var result = RuntimePlanning.Create(request, state.Installation,
            new(source.Source.SourceId, source.Source.Publisher, source.Source.Enabled, RuntimeTrust.Confirmed, source.Source.TrustRevision),
            catalog, source.History.HighWater, DateTimeOffset.UtcNow, dependencies);
        if (result.Plan?.Release is { } release && request.Change == RuntimeChange.Update && state.Active is { } current
            && SingBoxNativeValidation.UpdateProblem(current.MeasuredVersion, release.OriginalVersion) is { } problem)
            return new(null, [problem], dependencies);
        return result;
    }

    internal async Task<RuntimeSwitchResult> ExecuteAsync(Guid operationId, RuntimeChangePlan plan,
        RuntimeAuthorizationChannel channel, CancellationToken ct, RuntimeStageReporter? progress = null)
    {
        // The channel descriptor never enters a frozen plan, durable journal, or idempotency hash.
        if (channel.OperationId != operationId) throw new RuntimePolicyException("runtime.authorization_unavailable");
        await EnsureStateAsync(ct);
        // A cancellation request arrives through a second Helper process and is carried into preparation by
        // this token. The transaction itself decides when it is too late to honour it.
        using var cancellation = SingBoxRuntimeCancellation.Watch(root, operationId, ct);
        // Removal never reads published credentials, so it does not depend on a live authorization channel.
        return plan.Request.Change == RuntimeChange.Uninstall
            ? await Removal(progress).ExecuteAsync(operationId, plan, cancellation.Token)
            : await Switch(channel, progress).ExecuteAsync(operationId, plan, cancellation.Token);
    }

    /// <summary>
    /// Records a cancellation request for one transaction. It reads no state and takes no instance lock, so it
    /// still works while the transaction owns both; the operation's own receipt stays the only authority on
    /// whether the request was early enough to change the outcome.
    /// </summary>
    internal RuntimeCancelRequestResult CancelRequest(Guid operationId)
    {
        if (operationId == Guid.Empty) throw new RuntimePolicyException("runtime.operation_id_invalid");
        SingBoxRuntimeCancelChannel.Request(root, operationId);
        return new(operationId, true);
    }

    internal async Task<RuntimeSwitchResult?> RecoverAsync(RuntimeAuthorizationChannel channel, Guid operationId, CancellationToken ct,
        RuntimeStageReporter? progress = null)
    {
        await EnsureStateAsync(ct);
        return states.Read()?.Removal is not null
            ? await Removal(progress).RecoverAsync(operationId, ct)
            : await Switch(channel, progress).RecoverAsync(operationId, ct);
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
        catch (RuntimePolicyException ex) { return snapshot with { ServiceProblem = ex.ProblemCode }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        { return snapshot with { ServiceProblem = "runtime.service_observation_failed" }; }
    }

    /// <summary>
    /// Emergency stop of the managed service only. It reads no published credential, so it keeps working after
    /// they are revoked, and it never touches the transaction ledger: the operation stays unresolved.
    /// </summary>
    internal async Task<RuntimeRecoverySnapshot> StopAsync(Guid operationId, CancellationToken ct)
    {
        var snapshot = RuntimeArchiveRecovery.Require(RuntimeArchiveRecovery.Describe(await EnsureStateAsync(ct)), operationId);
        // A unit this installation does not own must not be stopped by its emergency entry.
        var observed = await host.ObserveAsync(ct);
        if (observed.Exists)
        {
            await host.StopAsync(CancellationToken.None);
            try { await host.SetBootAsync(false, CancellationToken.None); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.state_unknown") { }
            observed = await host.ObserveAsync(ct);
        }
        if (observed.Running) throw new RuntimePolicyException("runtime.recovery_stop_failed");
        return snapshot with { Service = new(observed.Exists, observed.Running, observed.BootEnabled) };
    }

    /// <summary>Closes an unrecoverable transaction. The stop is re-observed here instead of trusted from an earlier call.</summary>
    internal async Task<RuntimeSwitchResult> CloseAsync(Guid operationId, CancellationToken ct)
    {
        await EnsureStateAsync(ct);
        var observed = await host.ObserveAsync(ct);
        using var session = states.Acquire();
        return RuntimeArchiveRecovery.Close(session, operationId, confirmed: true,
            new(observed.Exists, observed.Running, observed.BootEnabled));
    }

    private RuntimeArchiveRemoval Removal(RuntimeStageReporter? progress) =>
        new(states, new SingBoxRemovalAdapter(root, host, protect), progress);

    private RuntimeArchiveSwitch Switch(RuntimeAuthorizationChannel channel, RuntimeStageReporter? progress) => new(states,
        new SingBoxArchiveAdapter(root, states, sources, new RuntimeArchiveTransport(), host, new Authorization(channel), protect, progress, environment), progress);

    private async Task<RuntimeArchiveState> EnsureStateAsync(CancellationToken ct)
    {
        if (SingBoxManifest.Current is null) throw new RuntimePolicyException("runtime.platform_unsupported");
        var target = new RuntimeTarget(SingBoxManifest.PlatformName, SingBoxManifest.ArchitectureName, null, null,
            RuntimeInstallMode.Archive, OperatingSystem.IsWindows() ? "scm" : "systemd");
        var hostId = HostIdentity();
        var existing = states.Read();
        if (existing is not null)
        {
            if (existing.Installation.HostId != hostId || existing.Installation.RuntimeId != "sing-box" || existing.Installation.Target != target)
                throw new RuntimePolicyException("runtime.installation_mismatch");
            return existing;
        }
        protect(root);
        var ownership = RuntimeOwnership.Absent;
        try { if ((await host.ObserveAsync(ct)).Exists) ownership = RuntimeOwnership.External; }
        catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.external_installation") { ownership = RuntimeOwnership.External; }
        if (ownership == RuntimeOwnership.Absent && (File.Exists(Path.Combine(root, "config.json"))
            || File.Exists(Path.Combine(root, OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box"))
            || File.Exists(Path.Combine(root, "installed.json")))) ownership = RuntimeOwnership.Unknown;
        return states.Initialize(new(Guid.NewGuid(), hostId, "sing-box", 0, ownership, target, null, false), null);
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

    private sealed class Authorization(RuntimeAuthorizationChannel channel) : ISingBoxArchiveAuthorization
    {
        public Task<SingBoxCurrentAuthorization> ReadAsync(CancellationToken ct) => RuntimeAuthorizationClient.ReadAsync(channel, channel.OperationId, ct);
    }
}
