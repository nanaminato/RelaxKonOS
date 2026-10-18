using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.ProxyServer;


/// <summary>
/// The cancellation channel between two Helper processes and the operation shape that carries it.
/// The transaction's own cancellation boundary is covered by the switch fixture; this file proves the
/// request can actually reach a running root transaction without touching its lock or its ledger.
/// </summary>
internal static class CancellationChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        var root = Path.Combine(Path.GetTempPath(), "relaxkonos-runtime-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            void Protect(string path) => Directory.CreateDirectory(path);
            var runtimeRoot = Path.Combine(root, "runtime");
            var states = new RuntimeArchiveStateStore(Path.Combine(runtimeRoot, "state"), Protect);
            var sources = new RuntimeSourceStore(Path.Combine(root, "sources"), Protect);
            var lifecycle = new SingBoxRuntimeLifecycle(runtimeRoot, states, sources, new SilentHost(), Protect);
            SingBoxRuntimeLifecycle Create() => lifecycle;

            var operationId = Guid.NewGuid();
            lifecycle.CancelRequest(operationId);
            Check(RuntimeCancelChannel.Matches(runtimeRoot, operationId),
                "A cancellation request is recorded for the exact transaction that asked for it");
            Check(!RuntimeCancelChannel.Matches(runtimeRoot, Guid.NewGuid()),
                "A recorded request is never honored by an unrelated transaction");
            await File.WriteAllTextAsync(RuntimeCancelChannel.Marker(runtimeRoot), "not-a-guid");
            Check(!RuntimeCancelChannel.Matches(runtimeRoot, operationId),
                "A malformed marker is treated as absent instead of aborting the running transaction");
            var second = Guid.NewGuid();
            lifecycle.CancelRequest(second);
            Check(RuntimeCancelChannel.Matches(runtimeRoot, second)
                && !RuntimeCancelChannel.Matches(runtimeRoot, operationId),
                "Only one request is held: the newest holds the single marker");
            RuntimeCancelChannel.Clear(runtimeRoot);
            Check(!RuntimeCancelChannel.Matches(runtimeRoot, second), "Clearing the request is observable");

            var cancelRequest = new PrivilegedOperationRequest(PrivilegedOperationKind.SingBoxRuntimeCancel,
                OperationId: Guid.NewGuid(), RuntimeLifecycle: new(CancelOperationId: operationId));
            var recorded = await SingBoxRuntimeOperations.ExecuteAsync(cancelRequest, Create);
            Check(recorded.Success && RuntimeCancelChannel.Matches(runtimeRoot, operationId),
                "The Helper records a cancellation request without reading or writing the transaction ledger");
            var result = JsonSerializer.Deserialize<RuntimeCancelRequestResult>(Convert.FromBase64String(recorded.OutputBase64!))!;
            Check(result.OperationId == operationId && result.Requested,
                "The reply names the transaction the request was recorded for");
            foreach (var invalid in new[]
            {
                cancelRequest with { RuntimeLifecycle = new(CancelOperationId: operationId, Confirmed: true) },
                cancelRequest with { RuntimeLifecycle = new() },
                cancelRequest with { RuntimeLifecycle = new(CancelOperationId: operationId, RecoveryOperationId: operationId) },
                cancelRequest with { Operation = PrivilegedOperationKind.SingBoxRuntimeExecute, RuntimeLifecycle = new(
                    Authorization: new(Guid.NewGuid(), Guid.NewGuid(), "sing-box", new string('0', 64)), FrozenPlan: new(
                        new(Guid.NewGuid(), "host", RuntimeChange.Install, 0, 0, 0, "release", "key"), "sing-box", null, null),
                    CancelOperationId: operationId) }
            })
                Check(!(await SingBoxRuntimeOperations.ExecuteAsync(invalid, Create)).Success,
                    "A cancellation field is never accepted by any other lifecycle verb");
            try
            {
                lifecycle.CancelRequest(Guid.Empty);
                throw new Exception("An empty operation id was accepted as a cancellation target");
            }
            catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "runtime.operation_id_invalid", "A cancellation must name one transaction"); }

            RuntimeCancelChannel.Clear(runtimeRoot);
            using (var watched = RuntimeCancellation.Watch(runtimeRoot, operationId, default))
            {
                // The realistic path: the request arrives while the transaction is already preparing.
                using var request = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var observed = Task.Run(async () =>
                {
                    while (!watched.Token.IsCancellationRequested && !request.IsCancellationRequested)
                        await Task.Delay(25);
                    return watched.Token.IsCancellationRequested;
                });
                lifecycle.CancelRequest(operationId);
                Check(await observed, "A request recorded during preparation cancels the running transaction's token");
            }
            Check(!File.Exists(RuntimeCancelChannel.Marker(runtimeRoot)),
                "Ending the transaction clears the request so the next one starts clean");

            using (var watched = RuntimeCancellation.Watch(runtimeRoot, operationId, default))
            {
                lifecycle.CancelRequest(Guid.NewGuid());
                await Task.Delay(TimeSpan.FromMilliseconds(600));
                Check(!watched.Token.IsCancellationRequested,
                    "A request for another transaction leaves the running one alone");
            }

            using var abandoned = new CancellationTokenSource();
            var watcher = RuntimeCancellation.Watch(runtimeRoot, operationId, abandoned.Token);
            abandoned.Cancel();
            Check(watcher.Token.IsCancellationRequested, "The caller's own token still ends the watch");
            watcher.Dispose();
        }
        finally { try { Directory.Delete(root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        return count;
    }

    /// <summary>Only service mutation is refused; the cancellation channel never needs the host.</summary>
    private sealed class SilentHost : ISingBoxArchiveHost
    {
        public Task<SingBoxServiceObservation> ObserveAsync(CancellationToken ct) => Task.FromResult(new SingBoxServiceObservation(false, true, false, false, null, null));
        public Task StopAsync(CancellationToken ct) => throw new Exception("No service mutation expected");
        public Task StartAsync(CancellationToken ct) => throw new Exception("No service mutation expected");
        public Task RemoveOwnedRegistrationAsync(CancellationToken ct) => throw new Exception("No service mutation expected");
        public Task ConfigureAsync(string binary, string configuration, string identity, bool bootEnabled, CancellationToken ct) => throw new Exception("No service mutation expected");
        public Task SetBootAsync(bool enabled, CancellationToken ct) => throw new Exception("No service mutation expected");
        public Task<bool> ListenersHealthyAsync(ProxyServerConfiguration published, CancellationToken ct) => throw new Exception("No service mutation expected");
    }
}
