using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Production composition for the one Helper-owned sing-box installation.</summary>
internal sealed class SingBoxRuntimeLifecycle(string root, RuntimeArchiveStateStore states, RuntimeSourceStore sources,
    ISingBoxArchiveHost host, Action<string> protect)
{
    internal static SingBoxRuntimeLifecycle Create() => new(SingBoxOperations.Root,
        new(Path.Combine(SingBoxOperations.Root, "state"), RuntimeSourceProtection.Protect),
        RuntimeSourceProtection.Store(), SingBoxOperations.CreateArchiveHost(), RuntimeSourceProtection.Protect);

    internal async Task<RuntimeInstallationSnapshot> QueryAsync(CancellationToken ct, Guid? receiptOperationId = null)
    {
        var state = await EnsureStateAsync(ct);
        var receipt = state.Receipts.FirstOrDefault(r => r.OperationId == receiptOperationId);
        return new(state.Installation, state.Active?.MeasuredVersion, state.Active?.RevisionId, state.ConfigurationIdentity,
            state.Pending?.OperationId, state.Pending?.Stage, state.Pending?.RecoveryProblemCode ?? state.Pending?.ProblemCode,
            receipt is null ? null : new(receipt.OperationId, receipt.Outcome, receipt.ProblemCode));
    }

    internal async Task<RuntimePlanResult> PlanAsync(string sourceId, RuntimePlanRequest request, CancellationToken ct)
    {
        await EnsureStateAsync(ct);
        using var session = states.Acquire();
        return Plan(sourceId, request, session.State!);
    }

    private RuntimePlanResult Plan(string sourceId, RuntimePlanRequest request, RuntimeArchiveState state)
    {
        if (request.Change == RuntimeChange.Uninstall) return new(null, ["runtime.change_unsupported"]);
        var source = sources.Read(sourceId) ?? throw new RuntimePolicyException("runtime.source_not_found");
        if (source.Source.RuntimeId != "sing-box") throw new RuntimePolicyException("runtime.source_invalid");
        var catalog = source.Cached?.Catalog ?? throw new RuntimePolicyException("runtime.catalog_missing");
        var result = RuntimePlanning.Create(request, state.Installation,
            new(source.Source.SourceId, source.Source.Publisher, source.Source.Enabled, RuntimeTrust.Confirmed, source.Source.TrustRevision),
            catalog, source.History.HighWater, DateTimeOffset.UtcNow);
        if (result.Plan?.Release is { } release && request.Change == RuntimeChange.Update && state.Active is { } current
            && SingBoxNativeValidation.UpdateProblem(current.MeasuredVersion, release.OriginalVersion) is { } problem)
            return new(null, [problem]);
        return result;
    }

    internal async Task<RuntimeSwitchResult> ExecuteAsync(Guid operationId, RuntimeChangePlan plan,
        RuntimeAuthorizationChannel channel, CancellationToken ct, Func<InstallationStage, Task>? progress = null)
    {
        // The channel descriptor never enters a frozen plan, durable journal, or idempotency hash.
        if (channel.OperationId != operationId) throw new RuntimePolicyException("runtime.authorization_unavailable");
        await EnsureStateAsync(ct);
        if (plan.Request.Change == RuntimeChange.Uninstall) throw new RuntimePolicyException("runtime.change_unsupported");
        return await Switch(channel, progress).ExecuteAsync(operationId, plan, ct);
    }

    internal async Task<RuntimeSwitchResult?> RecoverAsync(RuntimeAuthorizationChannel channel, Guid operationId, CancellationToken ct,
        Func<InstallationStage, Task>? progress = null)
    {
        await EnsureStateAsync(ct);
        return await Switch(channel, progress).RecoverAsync(operationId, ct);
    }

    private RuntimeArchiveSwitch Switch(RuntimeAuthorizationChannel channel, Func<InstallationStage, Task>? progress) => new(states,
        new SingBoxArchiveAdapter(root, states, sources, new RuntimeArchiveTransport(), host, new Authorization(channel), protect, progress), progress);

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
