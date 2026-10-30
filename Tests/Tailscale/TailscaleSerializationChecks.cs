using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tailscale;

internal static class TailscaleSerializationChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new Exception("Tailscale serialization assertion " + count); count++; }
        PrivilegedOperationRequest Envelope(PrivilegedOperationKind kind, Guid id) => new(kind,OperationId:id,
            Correlation:CorrelationContext.Create(id,"privileged.operation"));
        var folder = Directory.CreateTempSubdirectory("tailscale-serialization-");
        var tasks = new List<Task>();
        var native = new Native();
        try
        {
            var packageRoot = Path.Combine(folder.FullName, "package-ledger");
            var package = Directory.CreateDirectory(Path.Combine(packageRoot, "tailscale")).FullName;
            var operation = Path.Combine(package, "operation.json");
            var change = Path.Combine(package, "change.json");
            var target = new RelaxKonOS.Protocol.ManagedRuntimes.ComponentPackageTarget("tailscale", "1.104.4", "amd64", "official|resolute|main|amd64",
                "https://pkgs.tailscale.com/stable/ubuntu", "resolute", "main", "https://pkgs.tailscale.com/stable/ubuntu/pool/tailscale_1.104.4_amd64.deb", new('a', 64), 1024, []);
            var committedOperation = System.Text.Json.JsonSerializer.Serialize(new { ComponentId = "tailscale", OperationId = Guid.NewGuid(), Target = target, Committed = true, ExecutableSha256 = new string('b', 64) });
            var committedChange = System.Text.Json.JsonSerializer.Serialize(new { OperationId = Guid.NewGuid(), Target = target, Committed = true,
                Change = new RelaxKonOS.Protocol.ManagedRuntimes.ComponentPackageChange(RelaxKonOS.Protocol.ManagedRuntimes.RuntimeChange.Update, Guid.NewGuid(), "1.104.2") });
            Check(!TailscalePackageInterlock.HasPending(packageRoot, _ => { }));
            File.WriteAllText(operation, "{\"Committed\":false}");
            Check(TailscalePackageInterlock.HasPending(packageRoot, _ => { }));
            File.WriteAllText(operation, committedOperation);
            Check(!TailscalePackageInterlock.HasPending(packageRoot, _ => { }));
            File.WriteAllText(change, "{\"Committed\":false}");
            Check(TailscalePackageInterlock.HasPending(packageRoot, _ => { }));
            File.WriteAllText(change, committedChange);
            Check(!TailscalePackageInterlock.HasPending(packageRoot, _ => { }));
            Check(TailscalePackageInterlock.HasPending(packageRoot, _ => throw new UnauthorizedAccessException()));
            File.WriteAllText(change, "{\"Committed\":true,\"Committed\":false}");
            Check(TailscalePackageInterlock.HasPending(packageRoot, _ => { }));
            File.WriteAllText(change, "{}");
            Check(TailscalePackageInterlock.HasPending(packageRoot, _ => { }));
            File.WriteAllText(change, "{");
            Check(TailscalePackageInterlock.HasPending(packageRoot, _ => { }));
            var commands = new TailscaleLoginChecks.Commands("{}","https://login.tailscale.com/a/0123456789abcdef");
            var engine = new TailscaleControlOperations(folder.FullName,_=>{},native,new(commands.Run,()=>{}));
            var actor = Guid.NewGuid().ToString("D");
            foreach (var kind in new[] { PrivilegedOperationKind.TailscaleControlRead,PrivilegedOperationKind.TailscaleControlReceipt,
                PrivilegedOperationKind.TailscaleRecoveryRead,PrivilegedOperationKind.TailscaleRecoveryReceipt,
                PrivilegedOperationKind.TailscaleRecoveryClose,PrivilegedOperationKind.TailscaleControlExecute,
                PrivilegedOperationKind.TailscaleLoginObserve })
            {
                var id = Guid.NewGuid();
                var request = Envelope(kind,id);
                if (kind == PrivilegedOperationKind.TailscaleControlExecute) request = request with { TailscaleChange = new(id,native.Snapshot.Revision,TailscaleAction.Connect) };
                if (kind == PrivilegedOperationKind.TailscaleRecoveryClose) request = request with { TailscaleRecovery = new(new(id,native.Snapshot.Revision,true),actor) };
                Task<PrivilegedOperationResult> pending;
                using (var held = new FileStream(Path.Combine(folder.FullName,"native.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                {
                    var reads = native.Reads; var executions = native.Executions;
                    pending = engine.HandleAsync(request); tasks.Add(pending);
                    Check(!pending.IsCompleted && native.Reads == reads && native.Executions == executions);
                }
                await pending.WaitAsync(TimeSpan.FromSeconds(3));
                Check(pending.IsCompletedSuccessfully);
            }

            // A second Helper instance cannot see an in-flight intent or run a competing change outside the shared lock.
            native.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstId = Guid.NewGuid();
            var first = Envelope(PrivilegedOperationKind.TailscaleControlExecute,firstId) with
                { TailscaleChange = new(firstId,native.Snapshot.Revision,TailscaleAction.Disconnect) };
            var active = engine.HandleAsync(first); tasks.Add(active);
            Check(native.Executions == 2 && !active.IsCompleted);
            var second = new TailscaleControlOperations(folder.FullName,_=>{},native);
            var duplicate = second.HandleAsync(first); tasks.Add(duplicate);
            var read = second.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleControlRead,Guid.NewGuid())); tasks.Add(read);
            var otherId = Guid.NewGuid();
            var competing = second.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleControlExecute,otherId) with
                { TailscaleChange = new(otherId,native.Snapshot.Revision,TailscaleAction.Connect) }); tasks.Add(competing);
            var recovery = second.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleRecoveryRead,firstId)); tasks.Add(recovery);
            Check(!duplicate.IsCompleted && !read.IsCompleted && !competing.IsCompleted && !recovery.IsCompleted && native.Executions == 2);
            native.Hold.SetResult(true);
            var result = await active.WaitAsync(TimeSpan.FromSeconds(3));
            var repeated = await duplicate.WaitAsync(TimeSpan.FromSeconds(3));
            Check(result.TailscaleResult?.Completed == true && repeated.TailscaleResult == result.TailscaleResult && native.Executions == 2);
            Check((await read.WaitAsync(TimeSpan.FromSeconds(3))).TailscaleControl?.Connected == false);
            Check(!(await competing.WaitAsync(TimeSpan.FromSeconds(3))).Success && native.Executions == 2);
            Check(!(await recovery.WaitAsync(TimeSpan.FromSeconds(3))).Success);
        }
        finally
        {
            // All test-owned waits are bounded; never remove a directory under an active simulated mutation.
            native.Hold?.TrySetResult(true);
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
            folder.Delete(true);
        }
        Console.WriteLine($"{count} Tailscale shared native-lock checks passed.");
    }
    private sealed class Native : ITailscaleNativeControl
    {
        internal int Reads,Executions;
        internal TaskCompletionSource<bool>? Hold;
        internal TailscaleControlSnapshot Snapshot = new(new('A',64),false,true,new(true,false),true);
        public Task<TailscaleControlSnapshot> ReadAsync(CancellationToken ct) { Reads++; return Task.FromResult(Snapshot); }
        public Task<bool> IsQuiescentAsync(CancellationToken ct) => Task.FromResult(true);
        public async Task<bool> ApplyAsync(TailscalePrepareRequest request, CancellationToken ct)
        {
            Executions++;
            if (Hold is not null) await Hold.Task.WaitAsync(ct);
            Snapshot = Snapshot with { Connected = request.Action == TailscaleAction.Connect,Revision = new((char)('A' + Executions),64) };
            return true;
        }
    }
}
