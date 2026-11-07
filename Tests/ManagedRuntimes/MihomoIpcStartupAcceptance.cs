using Microsoft.AspNetCore.DataProtection;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy.Platform;

internal static class MihomoIpcStartupAcceptance
{
    internal static void RequirePrivateHost()
    {
        if (!OperatingSystem.IsLinux() || !Environment.IsPrivilegedProcess)
            throw new InvalidOperationException("Mihomo IPC acceptance requires private Linux systemd.");
        var mounts = File.ReadLines("/proc/self/mountinfo")
            .Where(line => line.Contains(" - tmpfs ", StringComparison.Ordinal))
            .Select(line => line.Split(' ')[4]).ToHashSet();
        if (!mounts.IsSupersetOf(["/var/lib", "/run", "/etc/relaxkonos", "/etc/systemd/system"]))
            throw new InvalidOperationException("Mihomo IPC acceptance requires private policy, state, units and sockets.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    internal static async Task RunAsync(MihomoRuntimeLifecycle lifecycle, MihomoPublishedRuntime original,
        Func<int, Task<bool>> probe, int port, string helperAssembly, string serverAssembly)
    {
        RequirePrivateHost();
        var work = Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SYSTEMD_WORK")
            ?? throw new InvalidOperationException("The dedicated runner directory is required.");
        var root = Directory.CreateDirectory(Path.Combine(work, "mihomo-ipc")).FullName;
        var statePath = Path.Combine(root, "server");
        var paths = new Paths(statePath);
        var protection = ManagedServiceChecks.OpenProtection(root);
        var content = Path.Combine(root, "full-server", "data");
        Directory.CreateDirectory(content);
        await File.WriteAllTextAsync(Path.Combine(content, "proxy-controller.secret"),
            protection.CreateProtector("RelaxKonOS.Proxy.ControllerSecret.v1").Protect(original.ControllerSecret));
        var secrets = new DataProtectionProxyControllerSecretStore(
            new HostingEnvironment { ContentRootPath = Path.GetDirectoryName(content)! }, protection);
        var store = new MihomoPublicationStore(paths, protection);
        var before = await lifecycle.QueryAsync(default);
        var identity = MihomoPublicationStore.Identity(original.Yaml);
        if (before.ConfigurationIdentity != identity || before.ActiveRevisionId is null)
            throw new InvalidOperationException("The original systemd publication is unavailable.");
        var bootstrap = Guid.NewGuid();
        await store.BeginAsync(bootstrap, 0, original, default);
        var configRoot = Directory.CreateDirectory(paths.GetProtectedConfigurationDirectory()).FullName;
        var active = Path.Combine(configRoot, "active.yaml");
        await File.WriteAllTextAsync(active, original.Yaml);
        var confirmed = await store.ConfirmAsync(bootstrap, identity, default);

        var uid = uint.Parse(Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SERVER_UID")!);
        var gid = uint.Parse(Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SERVER_GID")!);
        if (uid == 0) throw new InvalidOperationException("The full Server must use a non-root UID.");
        await NativeRuntimeProcess.RunAsync("/usr/bin/chown", ["-R", $"{uid}:{gid}", "--", configRoot], 256);
        var socket = Path.Combine(root, "ipc", "helper.sock");
        var config = Path.Combine(root, "helper.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new LinuxPrivilegedHelperConfiguration(socket, gid, [0, uid])));
        var transport = new UnixSocketPrivilegedOperationTransport(new() { SocketPath = socket },
            NullLogger<UnixSocketPrivilegedOperationTransport>.Instance);
        var operation = Guid.NewGuid();
        var candidate = MihomoConfigurationPolicy.Describe(original.Yaml.Replace("MATCH,DIRECT", "UNSUPPORTED-RULE,DIRECT", StringComparison.Ordinal)).Material;
        var plan = new RuntimeConfigurationPlan(before.Installation.InstallationId, before.Installation.HostId, "mihomo",
            before.Installation.StateRevision, before.ActiveRevisionId.Value, identity, MihomoPublicationStore.Identity(candidate.Yaml), "ipc-startup");
        var authorization = new MihomoRuntimeAuthorizationProvider(store, paths, secrets);
        var publication = new MihomoConfigurationPublicationService(store, authorization,
            new(new LoseConfigureReply(transport), store, authorization, new SystemMode(), new DirectDownloads()), paths);

        string firstLog;
        await using (var helper = await StartHelperAsync(helperAssembly, config, transport))
        {
            try
            {
                await publication.PublishAsync(operation, confirmed.Revision, plan, candidate, default);
                throw new InvalidOperationException("The response-loss barrier was not reached.");
            }
            catch (IOException error) when (error.Message == LoseConfigureReply.Message) { }
            firstLog = await helper.StopAsync();
        }
        var receipt = (await lifecycle.QueryAsync(default, operation)).Receipt;
        if (receipt is not { Outcome: RuntimeSwitchOutcome.Failed } || receipt.OperationId != operation
            || (await store.ReadAsync(default)).Pending?.OperationId != operation || !await probe(port)
            || !firstLog.Contains($"operation=MihomoRuntimeConfigure id={operation} ", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Real Helper compensation did not leave the original pending receipt.");

        var start = await ManagedServiceChecks.PrepareFullServerAsync(serverAssembly, socket, root);
        start.Environment["Proxy__Mihomo__Controller__Endpoint"] = $"http://127.0.0.1:{original.ControllerPort}/";
        var startupStore = new MihomoPublicationStore(new ProxyPlatformPaths(), protection);
        await CheckStartupAsync(start, operation, uid, ready: false);
        if ((await startupStore.ReadAsync(default)).Pending?.OperationId != operation)
            throw new InvalidOperationException("Blocked startup discarded the original Mihomo publication.");
        await using (var restarted = await StartHelperAsync(helperAssembly, config, transport))
        {
            await CheckStartupAsync(start, operation, uid, ready: true);
            var log = await restarted.StopAsync();
            if (log.Contains("operation=MihomoRuntimeConfigure ", StringComparison.Ordinal)
                || log.Contains("operation=MihomoRuntimeRecover ", StringComparison.Ordinal)
                || !log.Contains($"operation=MihomoRuntimeQuery id={operation} uid={uid} success=True", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The restarted Server did not exclusively query the original Helper receipt.");
        }
        var restored = await startupStore.ReadAsync(default);
        if (restored.Pending is not null || restored.Identity != identity || !await probe(port)
            || (await lifecycle.QueryAsync(default, operation)).Receipt != receipt)
            throw new InvalidOperationException("Recovered publication or original runtime traffic changed.");
        Console.WriteLine($"PASS MIHOMO IPC DISABLED STARTUP: original Failed receipt {operation}, one configure and no replay");

        var interrupted = Guid.NewGuid();
        var liveCandidate = MihomoConfigurationPolicy.Describe(original.Yaml.Replace($"mixed-port: {port}",
            $"mixed-port: {port + 6}", StringComparison.Ordinal)).Material;
        var observed = await lifecycle.QueryAsync(default);
        var interruptedPlan = plan with
        {
            ExpectedStateRevision = observed.Installation.StateRevision,
            CandidateIdentity = MihomoPublicationStore.Identity(liveCandidate.Yaml),
            IdempotencyKey = "ipc-interrupted"
        };
        await startupStore.BeginConfigurationAsync(interrupted, restored.Revision, liveCandidate, interruptedPlan, default);
        await NativeRuntimeProcess.RunAsync("/usr/bin/chown", ["-R", $"{uid}:{gid}", "--", ManagedServiceChecks.FullServerState], 256);
        var helperState = new RuntimeArchiveStateStore(Path.Combine(LinuxMihomoArchiveHost.Root, "state"), RuntimeSourceProtection.Protect);
        var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var helper = await StartHelperAsync(helperAssembly, config, transport))
        await using (var grant = new RuntimeAuthorizationServer<MihomoConfigurationAuthorization>(interrupted, async ct =>
        {
            if (helperState.Read()?.Configuration?.Stage == RelaxKonOS.Protocol.Installations.InstallationStage.HealthChecking)
            {
                verifying.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return new(interrupted, DateTimeOffset.UtcNow.AddSeconds(30), original, liveCandidate);
        }))
        {
            var publishing = transport.ExecuteAsync(new(PrivilegedOperationKind.MihomoRuntimeConfigure, OperationId: interrupted,
                RuntimeLifecycle: new(Authorization: grant.Channel, ConfigurationPlan: interruptedPlan)));
            var barrier = verifying.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (await Task.WhenAny(barrier, publishing) == publishing)
                throw new InvalidOperationException("Helper returned before reaching the native health-check barrier.");
            await barrier;
            if (!await probe(port + 6))
                throw new InvalidOperationException("The candidate did not forward real traffic before Helper termination.");
            await helper.StopAsync();
            if ((await publishing.WaitAsync(TimeSpan.FromSeconds(10))).Success)
                throw new InvalidOperationException("The interrupted Helper unexpectedly returned a configure receipt.");
        }
        var unresolved = await lifecycle.QueryAsync(default, interrupted);
        if (unresolved.PendingOperationId != interrupted || unresolved.Receipt is not null || !await probe(port + 6))
            throw new InvalidOperationException("The actual Helper interruption did not leave a live candidate and original journal.");
        await CheckStartupAsync(start, interrupted, uid, ready: false);
        await using (var helper = await StartHelperAsync(helperAssembly, config, transport))
        {
            await CheckStartupAsync(start, interrupted, uid, ready: true);
            var log = await helper.StopAsync();
            if (log.Contains("operation=MihomoRuntimeConfigure ", StringComparison.Ordinal)
                || log.Split('\n').Count(line => line.Contains("event=request-completed operation=MihomoRuntimeRecover ", StringComparison.Ordinal)
                    && line.Contains($"uid={uid} success=True", StringComparison.Ordinal)) != 1)
                throw new InvalidOperationException("Disabled startup did not perform exactly one named compensation.");
        }
        var final = await lifecycle.QueryAsync(default, interrupted);
        if (final.Receipt is not { Outcome: RuntimeSwitchOutcome.Interrupted } recovered || recovered.OperationId != interrupted
            || final.PendingOperationId is not null || final.ConfigurationIdentity != identity
            || (await startupStore.ReadAsync(default)).Pending is not null || !await probe(port))
            throw new InvalidOperationException("Compensation did not durably restore the original native publication.");
        Console.WriteLine($"PASS MIHOMO IPC INTERRUPTED COMPENSATION: original Interrupted receipt {interrupted}, one recover and no configure replay");

        var tunPaths = new ProxyPlatformPaths();
        using var requests = new ControllerRequestCount();
        using var http = new HttpClient(requests, disposeHandler: false);
        var controller = new MihomoControllerClient(http, secrets,
            new() { Endpoint = new($"http://127.0.0.1:{original.ControllerPort}/") });
        var platform = new HostProxyNetworkSafetyPlatform();
        var safety = new ProxyTunSafetyService(tunPaths, platform,
            new MihomoTunRuntimeController(tunPaths, new ProxySettingsReader(tunPaths), controller, startupStore,
                new HostProxyTunDeviceObserver()));
        var managementAddress = IPAddress.Parse("192.0.2.3");
        var route = await platform.CaptureManagementRouteAsync(managementAddress, default)
            ?? throw new InvalidOperationException("The private systemd fixture has no management route.");
        var networkBefore = await ReadNetworkAsync();
        var pidBefore = await ReadRuntimePidAsync();
        if (!int.TryParse(pidBefore, out var runtimePid) || runtimePid <= 0)
            throw new InvalidOperationException("The confirmed Mihomo systemd process is not running.");
        var activeBefore = await File.ReadAllBytesAsync(active);
        var publicationBefore = await startupStore.ReadAsync(default);
        var rejected = await safety.EnableAsync(Guid.NewGuid(), managementAddress, default);
        var after = await lifecycle.QueryAsync(default, interrupted);
        var publicationAfter = await startupStore.ReadAsync(default);
        var activeAfter = await File.ReadAllBytesAsync(active);
        if (rejected != ProxyProblemCodes.NotSupported || await safety.CaptureRecoveryAsync(includeCurrentSession: true, default) is not null
            || requests.Count != 0 || !activeBefore.SequenceEqual(activeAfter)
            || after.Receipt != final.Receipt || after.Installation.StateRevision != final.Installation.StateRevision
            || after.ConfigurationIdentity != final.ConfigurationIdentity
            || publicationAfter.Revision != publicationBefore.Revision || publicationAfter.Identity != publicationBefore.Identity
            || publicationAfter.Pending is not null
            || await ReadNetworkAsync() != networkBefore || !await platform.VerifyManagementRouteAsync(route, default)
            || await ReadRuntimePidAsync() != pidBefore || !await probe(port))
            throw new InvalidOperationException("Rejected TUN activation changed the confirmed native publication, network or recovery authority.");
        Console.WriteLine($"PASS MIHOMO CONFIRMED TUN GATE: original compensation {interrupted}, native PID preserved, no marker or controller request");
    }

    private static async Task<string> ReadNetworkAsync() =>
        await NativeRuntimeProcess.RunAsync("/usr/sbin/ip", ["-4", "rule", "show"], 4096)
        + await NativeRuntimeProcess.RunAsync("/usr/sbin/ip", ["-6", "rule", "show"], 4096)
        + string.Join(',', NetworkInterface.GetAllNetworkInterfaces().Select(item => item.Name).Order(StringComparer.Ordinal));

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static Task<string> ReadRuntimePidAsync() =>
        NativeRuntimeProcess.RunAsync("/usr/bin/systemctl", ["show", LinuxMihomoArchiveHost.Unit, "--property=MainPID", "--value"], 64);

    private sealed class ControllerRequestCount() : DelegatingHandler(new SocketsHttpHandler { UseProxy = false })
    {
        internal int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static async Task CheckStartupAsync(ProcessStartInfo start, Guid operation, uint uid, bool ready)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start full Server.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        try
        {
            while (!process.HasExited)
            {
                try
                {
                    using var response = await http.GetAsync(ManagedServiceChecks.FullServerUrl + "/healthz", deadline.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        if (!ready) throw new InvalidOperationException("Server admitted unknown Mihomo publication.");
                        var identity = File.ReadLines($"/proc/{process.Id}/status").Single(line => line.StartsWith("Uid:", StringComparison.Ordinal));
                        if (!identity.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1).All(value => value == uid.ToString()))
                            throw new InvalidOperationException("Server did not run under its non-root UID.");
                        return;
                    }
                }
                catch (HttpRequestException) { }
                await Task.Delay(100, deadline.Token);
            }
            if (ready || process.ExitCode == 0 || !(await errors).Contains(operation.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Unexpected full Server exit {process.ExitCode}.");
            Console.WriteLine("PASS MIHOMO IPC UNAVAILABLE: startup refused with original operation");
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (!(await output).Contains("Now listening on", StringComparison.Ordinal) && ready)
                Console.WriteLine(await errors);
        }
    }

    private static async Task<HelperProcess> StartHelperAsync(string assembly, string config, IPrivilegedOperationTransport transport)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in new[] { assembly, "--linux-service", "--config", config }) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start production Helper.");
        var helper = new HelperProcess(process, process.StandardError.ReadToEndAsync());
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!process.HasExited)
            {
                if ((await transport.ExecuteAsync(new(PrivilegedOperationKind.MihomoRuntimeQuery), deadline.Token)).Success)
                    return helper;
                await Task.Delay(100, deadline.Token);
            }
            throw new InvalidOperationException("Helper exited before readiness.");
        }
        catch { await helper.DisposeAsync(); throw; }
    }

    private sealed class HelperProcess(Process process, Task<string> stderr) : IAsyncDisposable
    {
        public async Task<string> StopAsync()
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return await stderr;
        }

        public async ValueTask DisposeAsync()
        {
            using (process) Console.WriteLine(await StopAsync());
        }
    }

    private sealed class LoseConfigureReply(IPrivilegedOperationTransport transport) : IPrivilegedOperationTransport
    {
        internal const string Message = "Lost real configure reply before Server publication confirmation";
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            var result = await transport.ExecuteAsync(request, ct);
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeConfigure && result.Success) throw new IOException(Message);
            return result;
        }
    }

    private sealed class Paths(string state) : IProxyPlatformPaths
    {
        public string GetStateDirectory() => state;
        public string GetProtectedConfigurationDirectory() => "/etc/relaxkonos/proxy";
        public string GetEngineVersionsDirectory(string engineId) => state;
        public string GetEngineDataDirectory(string engineId) => state;
        public string GetSanitizedLogDirectory() => state;
    }

    private sealed class SystemMode : IServerModeResolver
    {
        public ServerMode Mode => ServerMode.System;
        public bool Supports(ServerHostFeature feature) => feature == ServerHostFeature.PrivilegedOperations;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    }
}
