using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.ProxyServer;

internal static class ProxySystemdRecoveryChecks
{
    internal static async Task RunAsync(string helperAssembly, string serverAssembly, string executable)
    {
        if (!OperatingSystem.IsLinux() || !Environment.IsPrivilegedProcess)
            throw new InvalidOperationException("The systemd fixture requires Linux root inside its private namespace.");
        var mounts = File.ReadLines("/proc/self/mountinfo")
            .Where(line => line.Contains(" - tmpfs ", StringComparison.Ordinal)).Select(line => line.Split(' ')[4]).ToHashSet();
        if (!mounts.IsSupersetOf(["/var/lib", "/run", "/etc/systemd/system", "/etc/relaxkonos"]))
            throw new InvalidOperationException("The systemd fixture requires private state, units and sockets.");
        if (!File.ReadLines("/proc/self/mountinfo").Any(line =>
            line.Split(' ')[4] == "/etc" && line.Contains(" - overlay ", StringComparison.Ordinal)))
            throw new InvalidOperationException("The guard test requires a private writable /etc overlay.");
        const string runtimeRoot = LinuxSingBoxArchiveHost.Root;
        if (Directory.Exists(runtimeRoot)) throw new InvalidOperationException("The private runtime must be absent.");
        var work = Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SYSTEMD_WORK");
        if (work is null || Path.GetDirectoryName(work) != "/tmp"
            || !Path.GetFileName(work).StartsWith("relaxkonos-mb04-systemd.", StringComparison.Ordinal))
            throw new InvalidOperationException("The systemd fixture requires the runner's dedicated work directory.");
        var root = Directory.CreateDirectory(Path.Combine(work, "fixture")).FullName;
        var states = new RuntimeArchiveStateStore(runtimeRoot + "/state", RuntimeSourceProtection.Protect);
        using var traffic = new ProxyFullServerTrafficProbe();
        await using var guard = await ProxySystemdGuardProbe.StartAsync(work, root);
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var inboundPort = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        var inboundId = Guid.NewGuid();
        var expired = new ProxyServerSecret(Guid.NewGuid(), [inboundId], Guid.NewGuid().ToString("D"));
        var next = expired with { Id = Guid.NewGuid(), Value = Guid.NewGuid().ToString("D") };
        var retained = expired with { Id = Guid.NewGuid(), Value = Guid.NewGuid().ToString("D") };
        var material = traffic.CreateMaterial(new(new(1,
            [new(inboundId, "systemd-fixture", ProxyServerProtocol.VlessTls, "127.0.0.1",
                inboundPort, "127.0.0.1", inboundPort, "proxy-fixture.example")]), [expired, next, retained], []));
        var binaryBytes = await File.ReadAllBytesAsync(executable);
        var version = (await SingBoxOperations.RunAsync(executable, ["version"]))
            .Split('\n', StringSplitOptions.TrimEntries)[0].Split(' ')[^1];
        var revisionId = Guid.NewGuid();
        var binary = runtimeRoot + "/versions/" + revisionId.ToString("N") + "/sing-box";
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        await File.WriteAllBytesAsync(binary, binaryBytes);
        File.SetUnixFileMode(binary, (UnixFileMode)493);
        var target = new RuntimeTarget("linux", RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "amd64",
            null, null, RuntimeInstallMode.Archive, "systemd");
        var artifact = new RuntimeArtifact("fixture-artifact", new string('c', 64), binaryBytes.Length, "tar.gz", "sing-box",
            "fixture-key", new(binaryBytes.Length, 8, [new("sing-box", binaryBytes.Length, Digest(binaryBytes), true)]));
        var release = new RuntimeRelease("fixture-release", "sing-box", "v" + version, version, RuntimeChannel.Stable,
            "fixture-source", target, artifact, SingBoxRuntimeSupport.Capability, true,
            RuntimeTrust.Confirmed, RuntimeEvidence.Unverified, DateTimeOffset.UtcNow);
        var hash = Digest(Encoding.UTF8.GetBytes(SingBoxConfiguration.Render(material)));
        await File.WriteAllTextAsync(runtimeRoot + "/config.json", SingBoxConfiguration.Render(material));
        File.SetUnixFileMode(runtimeRoot + "/config.json", (UnixFileMode)384);
        var initial = states.Initialize(new(Guid.NewGuid(), "isolated-systemd-host", "sing-box", 0,
            RuntimeOwnership.Absent, target, null, false), hash);
        using (var session = states.Acquire())
            session.Save(initial with
            {
                Installation = initial.Installation with { StateRevision = 1, Ownership = RuntimeOwnership.Managed,
                    ActiveReleaseId = release.ReleaseId },
                Active = new(revisionId, release, version, hash, 1, 1),
                ConfigurationIdentity = hash,
                Revisions = [new(revisionId, release, version, hash, 1, 1)]
            });

        var host = new LinuxSingBoxArchiveHost();
        Process? helper = null;
        Task<string>? helperOutput = null;
        Task<string>? helperErrors = null;
        try
        {
            await host.ConfigureAsync(binary, runtimeRoot + "/config.json", hash, false, default);
            await host.StartAsync(default);
            var configPath = root + "/helper.json";
            var socket = root + "/ipc/helper.sock";
            var uid = uint.Parse(Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SERVER_UID")!);
            var gid = uint.Parse(Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SERVER_GID")!);
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new LinuxPrivilegedHelperConfiguration(socket, gid, [0, uid])));
            File.SetUnixFileMode(configPath, (UnixFileMode)384);
            helper = await StartHelper();
            var transport = new UnixSocketPrivilegedOperationTransport(new() { SocketPath = socket },
                NullLogger<UnixSocketPrivilegedOperationTransport>.Instance);
            await guard.ApplyAsync(transport, enabled: true);
            await guard.VerifyTrafficAsync();
            var store = ManagedServiceChecks.OpenServerStore(root);
            var expiry = DateTimeOffset.UtcNow.AddSeconds(25);
            await store.WriteAsync(ProxyServerState.Empty with
            {
                Published = material,
                Credentials = material.Credentials.Select(grant => new StoredCredential(grant.Id, Guid.NewGuid(), "fixture",
                    grant.InboundIds.ToArray(), grant.Value, true, grant.Id == expired.Id ? DateTimeOffset.UtcNow.AddMinutes(-1)
                        : grant.Id == next.Id ? expiry : null)).ToArray()
            }, default);
            var before = states.Read()!.Installation.StateRevision;
            var expected = material with { Credentials = [next, retained] };
            var expectedHash = Digest(Encoding.UTF8.GetBytes(SingBoxConfiguration.Render(expected)));
            var workerStart = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true };
            foreach (var arg in new[] { typeof(ProxySystemdRecoveryChecks).Assembly.Location,
                "runtime-proxy-systemd-expiry-worker", socket, root }) workerStart.ArgumentList.Add(arg);
            using (var worker = Process.Start(workerStart) ?? throw new InvalidOperationException("Unable to start expiry worker."))
            {
                try
                {
                    var marker = await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    if (marker != "native-apply-committed" || worker.HasExited
                        || states.Read()!.ConfigurationIdentity != expectedHash
                        || (await store.ReadAsync(default)).PendingPublication is null)
                        throw new InvalidOperationException("The real Helper did not commit before the domain reply barrier.");
                    worker.Kill();
                    await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    if (worker.ExitCode == 0) throw new InvalidOperationException("The expiry worker did not crash.");
                }
                finally
                {
                    if (!worker.HasExited) worker.Kill();
                    await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }

            var applied = await host.ObserveAsync(default);
            if (!applied.Running || applied.AppliedConfigurationIdentity != expectedHash)
                throw new InvalidOperationException("The real systemd service did not apply the original candidate.");
            var pidBeforeCrash = await SingBoxOperations.RunAsync("/usr/bin/systemctl", ["show", LinuxSingBoxArchiveHost.Unit, "--property=MainPID", "--value"]);
            var firstHelper = await StopHelperAsync();
            if (firstHelper.ExitCode == 0 || !(await host.ObserveAsync(default)).Running
                || await SingBoxOperations.RunAsync("/usr/bin/systemctl", ["show", LinuxSingBoxArchiveHost.Unit, "--property=MainPID", "--value"]) != pidBeforeCrash)
                throw new InvalidOperationException("Helper termination stopped or replaced the independent runtime.");
            if (ApplyCount(firstHelper.Log) != 1) throw new InvalidOperationException("The original Helper must receive one apply.");
            using (var outage = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            using (var forwarded = await traffic.ForwardAsync(retained.Value, outage.Token))
                Console.WriteLine("PASS HELPER OUTAGE TRAFFIC: runtime accepted authenticated traffic with Helper stopped");

            await guard.VerifyTrafficAsync();
            await guard.VerifyPendingTunAsync();
            guard.SetCompetingValue();
            var recovered = await ManagedServiceChecks.RunFullServerAsync(serverAssembly, socket, root, next, retained, traffic, expiry,
                () => states.Read()!.Installation.StateRevision == before + 1
                    && states.Read()!.ConfigurationIdentity == expectedHash,
                async () => helper = await StartHelper(), guard.VerifyRecoveryAsync);
            var final = states.Read()!;
            var finalHash = Digest(Encoding.UTF8.GetBytes(SingBoxConfiguration.Render(material with { Credentials = [retained] })));
            if (!recovered || final.Installation.StateRevision != before + 2 || final.ConfigurationIdentity != finalHash
                || !(await host.ObserveAsync(default)).Running)
                throw new InvalidOperationException("The full Server replayed or lost a real systemd publication.");
            await guard.VerifyTrafficAsync();
            await guard.ApplyAsync(transport, enabled: false);
            guard.VerifyOriginalRestored();
            var secondHelper = await StopHelperAsync();
            if (ApplyCount(secondHelper.Log) != 1) throw new InvalidOperationException("Restarted Helper must receive only the later expiry apply.");
            Console.WriteLine("PASS PRODUCTION HELPER SYSTEMD: independent Helper crashed and restarted, runtime PID survived, original candidate confirmed, subsequent expiry and real traffic passed");

            async Task<Process> StartHelper()
            {
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { helperAssembly, "--linux-service", "--config", configPath }) start.ArgumentList.Add(arg);
                var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start production Helper.");
                helperOutput = process.StandardOutput.ReadToEndAsync();
                helperErrors = process.StandardError.ReadToEndAsync();
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var transport = new UnixSocketPrivilegedOperationTransport(new() { SocketPath = socket },
                    NullLogger<UnixSocketPrivilegedOperationTransport>.Instance);
                try
                {
                    while (!process.HasExited)
                    {
                        var observed = await transport.ExecuteAsync(new(PrivilegedOperationKind.ProxyServerStatus), startup.Token);
                        if (observed.Success) return process;
                        await Task.Delay(50, startup.Token);
                    }
                    throw new InvalidOperationException("Production Helper exited before readiness.");
                }
                catch
                {
                    if (!process.HasExited) process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Console.WriteLine(await helperOutput);
                    Console.WriteLine(await helperErrors);
                    process.Dispose();
                    throw;
                }
            }
        }
        finally
        {
            if (helper is not null) await StopHelperAsync();
            await host.StopAsync(default);
            await host.RemoveOwnedRegistrationAsync(default);
            if ((await host.ObserveAsync(default)).Exists)
                throw new InvalidOperationException("The private systemd registration remained after cleanup.");
            Console.WriteLine("PASS PRIVATE SYSTEMD CLEANUP: dedicated runtime and registration absent");
        }

        async Task<(int ExitCode, string Log)> StopHelperAsync()
        {
            using var process = helper ?? throw new InvalidOperationException("No owned Helper process.");
            helper = null;
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var log = await helperErrors!;
            Console.WriteLine(await helperOutput!);
            Console.WriteLine(log);
            return (process.ExitCode, log);
        }
    }

    private static int ApplyCount(string log) => log.Split('\n').Count(line =>
        line.Contains("event=request-completed operation=ProxyServerApply ", StringComparison.Ordinal));

    internal static async Task RunExpiryWorkerAsync(string socket, string root)
    {
        var transport = new ReplyBarrier(new UnixSocketPrivilegedOperationTransport(new() { SocketPath = socket },
            NullLogger<UnixSocketPrivilegedOperationTransport>.Instance));
        await new ProxyServerPublicationService(ManagedServiceChecks.OpenServerStore(root), transport, new SystemMode())
            .ReconcileExpiryAsync(default);
        throw new InvalidOperationException("The domain reply barrier was not reached.");
    }

    private sealed class ReplyBarrier(IPrivilegedOperationTransport transport) : IPrivilegedOperationTransport
    {
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            var result = await transport.ExecuteAsync(request, ct);
            if (request.Operation == PrivilegedOperationKind.ProxyServerApply && result.Success)
            {
                Console.WriteLine("native-apply-committed");
                await Task.Delay(Timeout.Infinite, ct);
            }
            return result;
        }
    }

    private sealed class SystemMode : IServerModeResolver
    {
        public RelaxKonOS.Protocol.Common.ServerMode Mode => RelaxKonOS.Protocol.Common.ServerMode.System;
        public bool Supports(ServerHostFeature feature) => feature == ServerHostFeature.Proxy;
        public RelaxKonOS.Protocol.Common.ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
