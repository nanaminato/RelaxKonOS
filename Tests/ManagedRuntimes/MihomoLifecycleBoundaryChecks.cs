using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;

internal static class MihomoLifecycleBoundaryChecks
{
    internal static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsLinux()) return 0;
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        var root = Path.Combine(Path.GetTempPath(), "mihomo-boundary-" + Guid.NewGuid().ToString("N"));
        void Protect(string path) => Directory.CreateDirectory(path);
        var states = new RuntimeArchiveStateStore(Path.Combine(root, "state"), Protect);
        var host = new Host();
        var lifecycle = new MihomoRuntimeLifecycle(root, states, new(Path.Combine(root, "sources"), Protect), host, Protect);
        var created = 0;
        MihomoRuntimeLifecycle Create() { created++; return lifecycle; }
        var id = Guid.NewGuid();
        var channel = new RuntimeAuthorizationChannel(Guid.NewGuid(), id, "mihomo", new string('0', 64));
        var query = new PrivilegedOperationRequest(PrivilegedOperationKind.MihomoRuntimeQuery, OperationId: id);
        var uninstall = new RuntimeChangePlan(new(Guid.NewGuid(), "host", RuntimeChange.Uninstall, 0, 0, 0, null, "key"), "mihomo", null, null);
        try
        {
            foreach (var invalid in new[]
            {
                query with { Path = "/host-file" }, query with { RuntimeSourceId = "source" },
                query with { RuntimeLifecycle = new() }, query with { OperationId = Guid.Empty }, query with { OperationId = null },
                query with { Operation = PrivilegedOperationKind.MihomoRuntimePlan, RuntimeLifecycle = new(SourceId: "../other") },
                query with { Operation = PrivilegedOperationKind.MihomoRuntimeExecute, RuntimeLifecycle = new(Authorization: channel, FrozenPlan: uninstall with { RuntimeId = "sing-box" }) },
                query with { Operation = PrivilegedOperationKind.MihomoRuntimeExecute, RuntimeLifecycle = new(Authorization: channel with { RuntimeId = "sing-box" }, FrozenPlan: uninstall) },
                query with { Operation = PrivilegedOperationKind.MihomoRuntimeExecute, RuntimeLifecycle = new(Authorization: channel with { OperationId = Guid.NewGuid() }, FrozenPlan: uninstall) },
                query with { Operation = PrivilegedOperationKind.MihomoRuntimeExecute, RuntimeLifecycle = new(Authorization: channel, FrozenPlan: uninstall, Confirmed: true) },
                query with { Operation = PrivilegedOperationKind.MihomoRuntimeRecoveryClose, RuntimeLifecycle = new(RecoveryOperationId: id) },
                query with { Operation = PrivilegedOperationKind.MihomoRuntimeCancel, RuntimeLifecycle = new(CancelOperationId: id, Confirmed: true) },
            }) Check((await MihomoRuntimeOperations.ExecuteAsync(invalid, Create)).ProblemCode == PrivilegedProblemCode.InvalidRequest,
                "Mihomo rejects mixed, wrong-runtime or unconfirmed privileged requests");
            Check(created == 0 && !Directory.Exists(root), "Invalid requests never construct a lifecycle or touch runtime state");
            var result = await MihomoRuntimeOperations.ExecuteAsync(query, Create);
            var snapshot = JsonSerializer.Deserialize<RuntimeInstallationSnapshot>(Convert.FromBase64String(result.OutputBase64!))!;
            Check(result.Success && snapshot.Installation.Ownership == RuntimeOwnership.Absent && created == 1,
                "Valid query records a host-bound observed absent installation");
            host.Unavailable = true;
            var unknownStates = new RuntimeArchiveStateStore(Path.Combine(root, "unknown", "state"), Protect);
            var unavailable = new MihomoRuntimeLifecycle(Path.Combine(root, "unknown"), unknownStates,
                new(Path.Combine(root, "unknown-sources"), Protect), host, Protect);
            var failed = await MihomoRuntimeOperations.ExecuteAsync(query, () => unavailable);
            Check(!failed.Success && failed.Error == "runtime.state_unknown" && unknownStates.Read() is null,
                "Failed service observation never becomes an absent installation");
            host.Unavailable = false;
            var cancellation = await MihomoRuntimeOperations.ExecuteAsync(query with { Operation = PrivilegedOperationKind.MihomoRuntimeCancel,
                RuntimeLifecycle = new(CancelOperationId: id) }, Create);
            Check(cancellation.Success && RuntimeCancelChannel.Matches(root, id), "Dedicated cancellation records only its named operation request");
            Check(states.Read()!.Pending is null && !states.Read()!.Installation.RecoveryRequired,
                "A cancellation request does not manufacture a transaction outcome");
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new Exception("Mihomo boundary cleanup escaped its root");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return count;
    }

    private sealed class Host : IMihomoArchiveHost
    {
        internal bool Unavailable;
        public Task<MihomoServiceObservation> ObserveAsync(CancellationToken ct) => Unavailable
            ? throw new RuntimePolicyException("runtime.state_unknown") : Task.FromResult(new MihomoServiceObservation(false, true, false, false, null, null, null));
        public Task StopAsync(CancellationToken ct) => throw new Exception("Unexpected service mutation");
        public Task ConfigureAsync(string binary, string data, string configuration, string identity, bool boot, CancellationToken ct) => throw new Exception("Unexpected service mutation");
        public Task SetBootAsync(bool enabled, CancellationToken ct) => throw new Exception("Unexpected service mutation");
        public Task StartAsync(CancellationToken ct) => throw new Exception("Unexpected service mutation");
        public Task RemoveOwnedRegistrationAsync(CancellationToken ct) => throw new Exception("Unexpected service mutation");
        public Task<bool> ListenersHealthyAsync(MihomoPublishedRuntime published, CancellationToken ct) => throw new Exception("Unexpected health mutation");
    }
}
