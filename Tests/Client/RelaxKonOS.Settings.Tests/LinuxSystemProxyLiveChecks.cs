extern alias Server;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Server::RelaxKonOS.Server.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.Protocol.Proxy;
using Server::RelaxKonOS.Server.Privileged;
using Server::RelaxKonOS.Server.Proxy;
using Server::RelaxKonOS.Server.Proxy.Mihomo;
using Server::RelaxKonOS.Server.Proxy.Platform;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;

internal static class LinuxSystemProxyLiveChecks
{
    private const string EnvironmentFile = "/etc/environment";
    private const string SocketPath = "/run/rk-proxy-fixture/helper.sock";
    private const string Journal = "/var/lib/relaxkonos-system-proxy/recovery.json";
    private const string Original = "RELAXKONOS_PROXY_FIXTURE=isolated\nhttp_proxy=http://127.0.0.1:1234\n";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);
    private static readonly LinuxSystemProxyConfiguration Desired = new(true, "127.0.0.1", 17890, true, "");
    private static readonly LinuxSystemProxyConfiguration TrackedDesired = Desired with { Port = 17891 };
    private static readonly Guid TrackedOperation = Guid.Parse("630a7065-c07e-4f8b-97c3-464c047397c8");

    internal static async Task RunAsync(string hostMountNamespace, string hostNetworkNamespace, string mode, uint serverUid, uint socketGid)
    {
        RequireIsolation(hostMountNamespace, hostNetworkNamespace);
        if (mode == "--linux-system-proxy-live-tracked-write")
        {
            RequireServerUid(serverUid);
            await DropTrackedResponseAsync();
            return;
        }
        if (mode == "--linux-system-proxy-live-tracked-read")
        {
            await VerifyTrackedReceiptAsync();
            return;
        }
        if (mode == "--linux-system-proxy-live-guard")
        {
            RequireServerUid(serverUid);
            await RunGuardAsync();
            return;
        }
        if (mode == "--linux-system-proxy-live-denied")
        {
            var denied = await CreateTransport().ExecuteAsync(new(PrivilegedOperationKind.LinuxSystemProxyApply, LinuxSystemProxy: Desired));
            // Closing a rejected socket before reading Hello can surface as a reset to this client.
            // The launcher independently requires the server's peer-rejection event.
            if (denied.Success || denied.ProblemCode is not (PrivilegedProblemCode.AccessDenied or PrivilegedProblemCode.HelperUnavailable))
                throw new InvalidOperationException($"Unexpected rejected peer result: {denied.ProblemCode}.");
            return;
        }
        if (mode == "--linux-system-proxy-live-worker")
        {
            RequireServerUid(serverUid);
            await ApplyAsync(Desired);
            Console.WriteLine("PROXY_APPLIED");
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return;
        }

        if (File.ReadAllText(EnvironmentFile) != Original || File.Exists(Journal))
            throw new InvalidOperationException("Expected a fresh isolated proxy environment.");
        var otherUid = serverUid == 65534 ? 65533u : 65534u;
        using (var rejected = StartPeer("--linux-system-proxy-live-denied", otherUid, socketGid, hostMountNamespace, hostNetworkNamespace, serverUid))
        {
            await rejected.WaitForExitAsync().WaitAsync(Budget);
            if (rejected.ExitCode != 0 || File.ReadAllText(EnvironmentFile) != Original || File.Exists(Journal))
                throw new InvalidOperationException("An unlisted UID reached the proxy writer.");
        }
        using (var original = StartPeer("--linux-system-proxy-live-worker", serverUid, socketGid, hostMountNamespace, hostNetworkNamespace, serverUid))
        {
            try
            {
                var ready = await original.StandardOutput.ReadLineAsync().WaitAsync(Budget);
                var applied = LinuxEnvironmentDocument.Parse(File.ReadAllText(EnvironmentFile)).Values;
                if (ready != "PROXY_APPLIED" || original.HasExited || !File.Exists(Journal)
                    || applied.GetValueOrDefault("http_proxy") != "http://127.0.0.1:17890"
                    || applied.GetValueOrDefault("https_proxy") != "http://127.0.0.1:17890")
                    throw new InvalidOperationException("Real proxy keys and journal must precede crash injection.");
                original.Kill();
                await original.WaitForExitAsync().WaitAsync(Budget);
                if (original.ExitCode == 0) throw new InvalidOperationException("Expected abnormal worker exit.");
            }
            finally
            {
                if (!original.HasExited) original.Kill();
                await original.WaitForExitAsync().WaitAsync(Budget);
            }
        }

        const string external = "http://127.0.0.1:2345";
        var target = LinuxSystemProxyOperations.EnvironmentTarget;
        var baseline = LinuxEnvironmentOperations.Execute(new(PrivilegedOperationKind.HostEnvironmentRead, EnvironmentTarget: target));
        var edit = LinuxEnvironmentOperations.Execute(new(PrivilegedOperationKind.HostEnvironmentApply,
            EnvironmentTarget: target, ExpectedRevision: baseline.HostEnvironment?.Revision,
            EnvironmentChange: new([new("http_proxy", EnvironmentMutationKind.Set, external),
                new("RELAXKONOS_EXTERNAL_EDIT", EnvironmentMutationKind.Set, "kept")], ConfirmHighImpact: true)));
        if (!baseline.Success || !edit.Success) throw new InvalidOperationException("Could not apply the competing environment edit.");
        await ApplyAsync(Desired with { Enabled = false });
        var restored = File.ReadAllText(EnvironmentFile);
        var values = LinuxEnvironmentDocument.Parse(restored).Values;
        if (values.GetValueOrDefault("http_proxy") != external || values.ContainsKey("https_proxy")
            || values.GetValueOrDefault("RELAXKONOS_EXTERNAL_EDIT") != "kept" || File.Exists(Journal))
            throw new InvalidOperationException("Recovery must restore owned proxy keys while preserving external edits.");
        await ApplyAsync(Desired with { Enabled = false });
        if (File.ReadAllText(EnvironmentFile) != restored || File.Exists(Journal))
            throw new InvalidOperationException("Repeated recovery changed the environment again.");
        await VerifyGuardAsync(serverUid, socketGid, hostMountNamespace, hostNetworkNamespace);
        Console.WriteLine("PASS LINUX SYSTEM PROXY LIVE: process killed, original journal restored once, external edit preserved, non-root Server UID authorized, unlisted UID rejected, non-root guard no-op verified over independent Helper IPC");
    }

    private static async Task DropTrackedResponseAsync()
    {
        using var budget = new CancellationTokenSource(Budget);
        var ct = budget.Token;
        using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), ct);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var hello = PrivilegedHelperHandshake.Hello();
            await PrivilegedHelperSocketFraming.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(hello), ct);
            var reply = await PrivilegedHelperSocketFraming.ReadAsync(stream, PrivilegedHelperTransportProtocol.MaximumFrameBytes, ct);
            var welcome = JsonSerializer.Deserialize<PrivilegedHelperHandshake>(reply ?? throw new IOException("Missing Helper welcome."));
            if (welcome is null || !welcome.IsValid(hello) || welcome.Type != PrivilegedHelperHandshake.WelcomeType)
                throw new InvalidOperationException("Could not open the private Helper connection.");
            await PrivilegedHelperSocketFraming.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(
                new PrivilegedOperationRequest(PrivilegedOperationKind.LinuxSystemProxyApplyTracked,
                    LinuxSystemProxy: TrackedDesired, OperationId: TrackedOperation,
                    Correlation: CorrelationContext.Create(TrackedOperation, "privileged.operation"))), ct);
            socket.Shutdown(SocketShutdown.Both); // The caller never reads the terminal response.
        }
        while (true)
        {
            var observed = await TrackedAsync(PrivilegedOperationKind.LinuxSystemProxyReceipt, TrackedOperation, TrackedDesired, ct);
            if (observed is { Success: true, LinuxSystemProxyReceipt: { Applied: true, Current: true } receipt }
                && receipt.OperationId == TrackedOperation)
                break;
            if (observed.ProblemCode == PrivilegedProblemCode.Conflict || observed.LinuxSystemProxyReceipt?.Applied == true)
                throw new InvalidOperationException("The original tracked receipt or its current effect did not match.");
            await Task.Delay(40, ct);
        }
        if (LinuxEnvironmentDocument.Parse(File.ReadAllText(EnvironmentFile)).Values.GetValueOrDefault("http_proxy") != "http://127.0.0.1:17891")
            throw new InvalidOperationException("The tracked operation did not change the real proxy endpoint.");
    }

    private static async Task VerifyTrackedReceiptAsync()
    {
        using var budget = new CancellationTokenSource(Budget);
        var ct = budget.Token;
        var before = File.ReadAllBytes(EnvironmentFile);
        var timestamp = File.GetLastWriteTimeUtc(EnvironmentFile);
        var journal = File.ReadAllBytes(Journal);
        var terminal = new LinuxSystemProxyReceipt(TrackedOperation, Applied: true, Current: true);
        var observed = await TrackedAsync(PrivilegedOperationKind.LinuxSystemProxyReceipt, TrackedOperation, TrackedDesired, ct);
        var duplicate = await TrackedAsync(PrivilegedOperationKind.LinuxSystemProxyApplyTracked, TrackedOperation, TrackedDesired, ct);
        var mismatch = await TrackedAsync(PrivilegedOperationKind.LinuxSystemProxyApplyTracked, TrackedOperation, Desired, ct);
        var missing = await TrackedAsync(PrivilegedOperationKind.LinuxSystemProxyReceipt, Guid.NewGuid(), TrackedDesired, ct);
        if (!observed.Success || observed.LinuxSystemProxyReceipt != terminal || !duplicate.Success || duplicate.LinuxSystemProxyReceipt != terminal
            || mismatch.Success || mismatch.ProblemCode != PrivilegedProblemCode.Conflict || !missing.Success || missing.LinuxSystemProxyReceipt is not null
            || !File.ReadAllBytes(EnvironmentFile).SequenceEqual(before) || File.GetLastWriteTimeUtc(EnvironmentFile) != timestamp
            || !File.ReadAllBytes(Journal).SequenceEqual(journal))
            throw new InvalidOperationException("The restarted Helper failed original receipt, duplicate, or mismatch checks.");

        const string external = "http://127.0.0.1:2345";
        var target = LinuxSystemProxyOperations.EnvironmentTarget;
        var state = LinuxEnvironmentOperations.Execute(new(PrivilegedOperationKind.HostEnvironmentRead, EnvironmentTarget: target));
        var edit = LinuxEnvironmentOperations.Execute(new(PrivilegedOperationKind.HostEnvironmentApply,
            EnvironmentTarget: target, ExpectedRevision: state.HostEnvironment?.Revision,
            EnvironmentChange: new([new("http_proxy", EnvironmentMutationKind.Set, external)], ConfirmHighImpact: true)));
        var drift = await TrackedAsync(PrivilegedOperationKind.LinuxSystemProxyReceipt, TrackedOperation, TrackedDesired, ct);
        var driftedDuplicate = await TrackedAsync(PrivilegedOperationKind.LinuxSystemProxyApplyTracked, TrackedOperation, TrackedDesired, ct);
        if (!state.Success || !edit.Success || !drift.Success || drift.LinuxSystemProxyReceipt != terminal with { Current = false }
            || !driftedDuplicate.Success || driftedDuplicate.LinuxSystemProxyReceipt != drift.LinuxSystemProxyReceipt
            || LinuxEnvironmentDocument.Parse(File.ReadAllText(EnvironmentFile)).Values.GetValueOrDefault("http_proxy") != external)
            throw new InvalidOperationException("Host drift was hidden or overwritten by original receipt recovery.");
        var disableId = Guid.NewGuid();
        var disabled = TrackedDesired with { Enabled = false };
        var disable = await TrackedAsync(PrivilegedOperationKind.LinuxSystemProxyApplyTracked, disableId, disabled, ct);
        var restored = await TrackedAsync(PrivilegedOperationKind.LinuxSystemProxyReceipt, disableId, disabled, ct);
        if (!disable.Success || !restored.Success || restored.LinuxSystemProxyReceipt != new LinuxSystemProxyReceipt(disableId, true, true)
            || File.Exists(Journal) || LinuxEnvironmentDocument.Parse(File.ReadAllText(EnvironmentFile)).Values.GetValueOrDefault("http_proxy") != external)
            throw new InvalidOperationException("Tracked disable did not preserve the external value or its original receipt.");
        Console.WriteLine($"PASS LINUX TRACKED SYSTEM PROXY: original {TrackedOperation}, response abandoned, Helper restarted, repeat observed without writes, external drift kept; disable {disableId}");
    }

    private static Task<PrivilegedOperationResult> TrackedAsync(PrivilegedOperationKind kind, Guid operation, LinuxSystemProxyConfiguration configuration,
        CancellationToken ct) => CreateTransport().ExecuteAsync(new(kind, LinuxSystemProxy: configuration, OperationId: operation), ct);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint EffectiveUid();

    private static void RequireServerUid(uint serverUid)
    {
        if (EffectiveUid() != serverUid || serverUid == 0) throw new InvalidOperationException("Expected the non-root Server UID.");
    }

    private static Process StartPeer(string mode, uint uid, uint gid, string hostMountNamespace, string hostNetworkNamespace, uint serverUid)
    {
        var start = new ProcessStartInfo("/usr/bin/setpriv") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardInput = true };
        foreach (var argument in new[] { $"--reuid={uid}", $"--regid={gid}", "--clear-groups", "--no-new-privs", "--",
            "/usr/bin/dotnet", Assembly.GetExecutingAssembly().Location, mode, hostMountNamespace, hostNetworkNamespace,
            serverUid.ToString(System.Globalization.CultureInfo.InvariantCulture), gid.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start isolated proxy IPC peer.");
    }

    private static async Task VerifyGuardAsync(uint serverUid, uint socketGid, string hostMountNamespace, string hostNetworkNamespace)
    {
        using var peer = StartPeer("--linux-system-proxy-live-guard", serverUid, socketGid, hostMountNamespace, hostNetworkNamespace, serverUid);
        async Task ExpectAsync(string expected)
        {
            var observed = await peer.StandardOutput.ReadLineAsync().WaitAsync(Budget);
            if (observed != expected) throw new InvalidOperationException($"Expected guard phase {expected}; got {observed}.");
        }

        try
        {
            await ExpectAsync("GUARD_APPLIED");
            var firstWrite = File.GetLastWriteTimeUtc(Journal);
            var original = File.ReadAllBytes(Journal);
            await ExpectAsync("GUARD_STABLE");
            if (File.GetLastWriteTimeUtc(Journal) != firstWrite || !File.ReadAllBytes(Journal).SequenceEqual(original))
                throw new InvalidOperationException("An unchanged guard pass rewrote its ownership journal.");

            WriteExternalProxy("http://127.0.0.1:3456");
            await peer.StandardInput.WriteLineAsync("DRIFT");
            await peer.StandardInput.FlushAsync();
            await ExpectAsync("GUARD_DRAINING");
            if (peer.HasExited || LinuxEnvironmentDocument.Parse(File.ReadAllText(EnvironmentFile)).Values
                .GetValueOrDefault("http_proxy") != "http://127.0.0.1:17890" || !File.Exists(Journal))
                throw new InvalidOperationException("The real Helper write and original journal must precede stop completion.");
            await peer.StandardInput.WriteLineAsync("DELIVER");
            await peer.StandardInput.FlushAsync();
            await ExpectAsync("GUARD_STOPPED");
            var guarded = LinuxEnvironmentDocument.Parse(File.ReadAllText(EnvironmentFile)).Values;
            if (guarded.GetValueOrDefault("http_proxy") != "http://127.0.0.1:17890")
                throw new InvalidOperationException("The enabled guard did not restore its configured proxy.");
            Console.WriteLine("PASS LINUX SYSTEM PROXY GUARD DRAIN: real Helper write completed; stop waited for the response and released the original gate without another apply");

            WriteExternalProxy("http://127.0.0.1:4567");
            await peer.StandardInput.WriteLineAsync("RESTORE");
            await peer.StandardInput.FlushAsync();
            await ExpectAsync("GUARD_RESTORED");
            await peer.WaitForExitAsync().WaitAsync(Budget);
            if (peer.ExitCode != 0 || File.Exists(Journal) || LinuxEnvironmentDocument.Parse(File.ReadAllText(EnvironmentFile))
                .Values.GetValueOrDefault("http_proxy") != "http://127.0.0.1:4567")
                throw new InvalidOperationException("Startup teardown must retain the newer external proxy setting.");
        }
        finally
        {
            if (!peer.HasExited) peer.Kill();
            await peer.WaitForExitAsync().WaitAsync(Budget);
        }
    }

    private static async Task RunGuardAsync()
    {
        var paths = new FixturePaths();
        var reader = new GuardSettings();
        var transport = new CountingTransport(CreateTransport());
        var host = new HostSystemProxyService(transport);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.AddProxyRuntimeServices();
        builder.Services.AddSingleton<IProxyPlatformPaths>(paths);
        builder.Services.AddSingleton<IProxySettingsReader>(reader);
        var protection = new EphemeralDataProtectionProvider();
        var publication = new MihomoPublicationStore(paths, protection);
        builder.Services.AddSingleton<IDataProtectionProvider>(protection);
        builder.Services.AddSingleton(publication);
        builder.Services.AddSingleton<IHostSystemProxyService>(host);
        var reconciler = new MihomoSystemProxyReconciler(paths, reader, new UnusedControllerSecret(), host, publication);
        builder.Services.AddSingleton<IProxySystemProxyReconciler>(reconciler);
        var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
        var catalog = ServerModules.CreateManaged(new HashSet<string>());
        var modules = ServerModules.CreateManaged(catalog.KnownModuleIds.Where(id => id != ServerModules.ProxyId).ToHashSet(StringComparer.Ordinal));
        modules.ConfigureServices(builder, context);
        await using var owner = builder.Build();
        await modules.ValidateDeactivationAsync(owner.Services, context, default);
        if (owner.Services.GetService<IProxyEngineRegistry>() is not null)
            throw new InvalidOperationException("The disabled Proxy management graph was registered.");
        using var guard = ActivatorUtilities.CreateInstance<SystemProxyGuardHostedService>(owner.Services);

        await guard.StartAsync(default);
        try
        {
            await transport.Applied.Reader.ReadAsync().AsTask().WaitAsync(Budget);
            Console.WriteLine("GUARD_APPLIED");
            await transport.Applied.Reader.ReadAsync().AsTask().WaitAsync(Budget);
            Console.WriteLine("GUARD_STABLE");
            if (await Console.In.ReadLineAsync().WaitAsync(Budget) != "DRIFT")
                throw new InvalidOperationException("Missing isolated drift signal.");
            transport.DriftReady.SetResult();
            await transport.Applied.Reader.ReadAsync().AsTask().WaitAsync(Budget);
            using var deadline = new CancellationTokenSource();
            deadline.Cancel();
            var stopped = guard.StopAsync(deadline.Token);
            if (stopped.IsCompleted || guard.ExecuteTask is not { IsCompleted: false } || publication.Gate.CurrentCount != 0)
                throw new InvalidOperationException("Shutdown released the original guard task or publication gate before the Helper response.");
            Console.WriteLine("GUARD_DRAINING");
            if (await Console.In.ReadLineAsync().WaitAsync(Budget) != "DELIVER")
                throw new InvalidOperationException("Missing isolated Helper delivery signal.");
            transport.Deliver.SetResult();
            await stopped.WaitAsync(Budget);
            if (transport.Requests != 3 || publication.Gate.CurrentCount != 1)
                throw new InvalidOperationException("Guard shutdown must release the gate without dispatching another apply.");
        }
        finally
        {
            transport.DriftReady.TrySetResult();
            transport.Deliver.TrySetResult();
            await guard.StopAsync(default).WaitAsync(Budget);
        }

        Console.WriteLine("GUARD_STOPPED");
        if (await Console.In.ReadLineAsync().WaitAsync(Budget) != "RESTORE")
            throw new InvalidOperationException("Missing isolated restore signal.");
        reader.Enabled = false;
        if (await reconciler.ReconcileSystemProxyAsync(startup: true, default) is not null)
            throw new InvalidOperationException("Non-root startup teardown failed.");
        Console.WriteLine("GUARD_RESTORED");
    }

    private static void WriteExternalProxy(string value)
    {
        var target = LinuxSystemProxyOperations.EnvironmentTarget;
        var read = LinuxEnvironmentOperations.Execute(new(PrivilegedOperationKind.HostEnvironmentRead, EnvironmentTarget: target));
        var edit = LinuxEnvironmentOperations.Execute(new(PrivilegedOperationKind.HostEnvironmentApply,
            EnvironmentTarget: target, ExpectedRevision: read.HostEnvironment?.Revision,
            EnvironmentChange: new([new("http_proxy", EnvironmentMutationKind.Set, value)], ConfirmHighImpact: true)));
        if (!read.Success || !edit.Success) throw new InvalidOperationException("Could not write the competing proxy.");
    }

    private sealed class GuardSettings : IProxySettingsReader
    {
        internal bool Enabled = true;
        public Task<ProxySettingsDto> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProxySettingsDto(Enabled, false, false, false, false, "error", 17890,
                SystemProxy: new(GuardEnabled: true, GuardIntervalSeconds: 5)));
    }

    private sealed class CountingTransport(IPrivilegedOperationTransport inner) : IPrivilegedOperationTransport
    {
        internal readonly System.Threading.Channels.Channel<bool> Applied = System.Threading.Channels.Channel.CreateUnbounded<bool>();
        internal readonly TaskCompletionSource DriftReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Deliver = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Requests;

        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Operation != PrivilegedOperationKind.LinuxSystemProxyApply)
                throw new InvalidOperationException("Unexpected isolated guard operation.");
            var hold = Interlocked.Increment(ref Requests) == 3;
            if (hold) await DriftReady.Task.WaitAsync(cancellationToken);
            var result = await inner.ExecuteAsync(request, cancellationToken);
            if (!result.Success) throw new InvalidOperationException($"Isolated guard apply failed: {result.ProblemCode}");
            Applied.Writer.TryWrite(true);
            if (hold) await Deliver.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class UnusedControllerSecret : IProxyControllerSecretStore
    {
        public Task<string> GetOrCreateAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> ReadExistingAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixturePaths : IProxyPlatformPaths
    {
        public string GetStateDirectory() => "/run/rk-proxy-guard-state";
        public string GetEngineVersionsDirectory(string engineId) => throw new NotSupportedException();
        public string GetEngineDataDirectory(string engineId) => throw new NotSupportedException();
        public string GetProtectedConfigurationDirectory() => throw new NotSupportedException();
        public string GetSanitizedLogDirectory() => throw new NotSupportedException();
    }

    private static void RequireIsolation(string hostMountNamespace, string hostNetworkNamespace)
    {
        if (!OperatingSystem.IsLinux()
            || !IsDistinctNamespace("mnt", hostMountNamespace) || !IsDistinctNamespace("net", hostNetworkNamespace)
            || !File.Exists("/etc/relaxkonos-proxy-fixture") || !File.Exists("/var/lib/relaxkonos-proxy-fixture")
            || new LinuxSystemProxyBackend().DesktopTargets().Count != 0)
            throw new InvalidOperationException("The live system proxy test requires fresh private mount and network namespaces.");
    }

    private static bool IsDistinctNamespace(string name, string original)
    {
        var prefix = name + ":[";
        if (!original.StartsWith(prefix, StringComparison.Ordinal) || !original.EndsWith(']')) return false;
        var current = new FileInfo("/proc/self/ns/" + name).LinkTarget;
        return current is not null && current.StartsWith(prefix, StringComparison.Ordinal) && current != original;
    }

    private static UnixSocketPrivilegedOperationTransport CreateTransport() =>
        new(new PrivilegedHelperOptions { SocketPath = SocketPath, TimeoutSeconds = 5 },
            NullLogger<UnixSocketPrivilegedOperationTransport>.Instance);

    private static async Task ApplyAsync(LinuxSystemProxyConfiguration configuration)
    {
        var result = await CreateTransport().ExecuteAsync(new(PrivilegedOperationKind.LinuxSystemProxyApply, LinuxSystemProxy: configuration));
        if (!result.Success) throw new InvalidOperationException($"Isolated Linux system proxy operation failed: {result.ProblemCode}");
    }
}
