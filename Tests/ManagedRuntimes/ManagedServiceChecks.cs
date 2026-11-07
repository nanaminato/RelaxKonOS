using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.ProxyServer;
using RelaxKonOS.Server.HostMode;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.Server.Privileged;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.ProxyServer;

/// <summary>
/// Published-configuration and service control for an archive-managed installation. The native binary is
/// replaced by a recording seam; no third-party program or platform service is executed here.
/// </summary>
internal static class ManagedServiceChecks
{
    internal static async Task<int> RunAsync(string? executable = null)
    {
        if (executable is not null && (!OperatingSystem.IsLinux() || executable != "/usr/bin/sing-box"))
            throw new ArgumentException("Expected the installed Linux sing-box binary.");
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        var root = Path.Combine(Path.GetTempPath(), "relaxkonos-managed-service-" + Guid.NewGuid().ToString("N"));
        try
        {
            var states = new RuntimeArchiveStateStore(Path.Combine(root, "state"), path => Directory.CreateDirectory(path));
            var target = new RuntimeTarget(OperatingSystem.IsWindows() ? "windows" : "linux",
                RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "amd64", null, null, RuntimeInstallMode.Archive,
                OperatingSystem.IsWindows() ? "scm" : "systemd");
            var binaryName = "sing-box";
            var binaryBytes = executable is null ? Encoding.UTF8.GetBytes("fixture program") : await File.ReadAllBytesAsync(executable);
            var version = executable is null ? "1.14.3" : (await SingBoxOperations.RunAsync(executable, ["version"]))
                .Split('\n', StringSplitOptions.TrimEntries)[0].Split(' ')[^1];
            var entry = new RuntimeArchiveFile(binaryName, binaryBytes.Length, Digest(binaryBytes), true);
            var artifact = new RuntimeArtifact("fixture-artifact", new string('c', 64), Math.Max(4096, binaryBytes.Length), "tar.gz", binaryName,
                "fixture-key", new(Math.Max(1024 * 1024, binaryBytes.Length), 8, [entry]));
            var now = DateTimeOffset.UtcNow;
            var release = new RuntimeRelease("fixture-release", "sing-box", "v" + version, version, RuntimeChannel.Stable,
                "fixture-source", target, artifact, SingBoxRuntimeSupport.Capability, true, RuntimeTrust.Confirmed, RuntimeEvidence.Unverified, now);
            var revisionId = Guid.NewGuid();
            var directory = Path.Combine(root, "versions", revisionId.ToString("N"));
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, binaryName), binaryBytes);
            if (executable is not null && OperatingSystem.IsLinux())
                File.SetUnixFileMode(Path.Combine(directory, binaryName), UnixFileMode.UserRead | UnixFileMode.UserExecute);
            using var firstPort = new TcpListener(IPAddress.Loopback, 0);
            using var secondPort = new TcpListener(IPAddress.Loopback, 0);
            firstPort.Start();
            secondPort.Start();
            var materialA = Material(((IPEndPoint)firstPort.LocalEndpoint).Port);
            var materialB = Material(((IPEndPoint)secondPort.LocalEndpoint).Port);
            firstPort.Stop();
            secondPort.Stop();
            var identityA = Digest(Encoding.UTF8.GetBytes(SingBoxConfiguration.Render(materialA)));
            await File.WriteAllBytesAsync(Path.Combine(root, "config.json"), Encoding.UTF8.GetBytes(SingBoxConfiguration.Render(materialA)));
            var revision = new RuntimeStoredRevision(revisionId, release, version, identityA, 1, 1);
            var initial = states.Initialize(new(Guid.NewGuid(), "fixture-host", "sing-box", 0, RuntimeOwnership.Absent, target, null, false), identityA);
            using (var session = states.Acquire())
                session.Save(initial with
                {
                    Installation = initial.Installation with
                    {
                        StateRevision = initial.Installation.StateRevision + 1,
                        Ownership = RuntimeOwnership.Managed,
                        ActiveReleaseId = release.ReleaseId
                    },
                    Active = revision,
                    ConfigurationIdentity = identityA,
                    Revisions = [revision]
                });
            await using var host = new ServiceHost(executable is not null);
            ISingBoxNativeChecks natives = executable is null ? new NativeChecks() : new SingBoxNativeChecks();

            var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dropResponse = false;
            await using var socket = executable is not null && OperatingSystem.IsLinux()
                ? OpenSocket(root, states, host, natives, async (request, result) =>
                {
                    if (!dropResponse || request.Operation != PrivilegedOperationKind.ProxyServerApply || !result.Success) return;
                    committed.TrySetResult();
                    await releaseResponse.Task.WaitAsync(TimeSpan.FromSeconds(20));
                }) : null;
            Task<PrivilegedOperationResult> RequestAsync(PrivilegedOperationKind operation, ProxyServerHelperRequest? request) =>
                socket is null ? Send(operation, request, root, states, host, natives)
                    : SendAcrossProcessAsync(socket.SocketPath, new(operation, ProxyServer: request));

            // Status of a managed installation whose service was never registered.
            var absent = await RequestAsync(PrivilegedOperationKind.ProxyServerStatus, null);
            var status = Read<ProxyServerStatus>(absent);
            Check(status is { Supported: true, Installed: true, Running: false, Applied: false, RecoveryRequired: false, Version: not null }
                && status.Version == version && status.ConfigurationHash == identityA && status.ProblemCode is null,
                "Managed installation reports its measured version and applied identity without a registered service");

            // Validation only renders and checks; it never touches the service or the durable state.
            var revisionBefore = states.Read()!.Installation.StateRevision;
            var validate = await RequestAsync(PrivilegedOperationKind.ProxyServerValidate, new(Action: null, Material: materialB));
            Check(validate.Success && Read<ProxyServerValidation>(validate).Valid && host.ConfigureCalls == 0
                && states.Read()!.Installation.StateRevision == revisionBefore
                && Digest(await File.ReadAllBytesAsync(Path.Combine(root, "config.json"))) == identityA,
                "Validation uses the recorded program without publishing the candidate");

            // First publication registers the recorded program and commits the new configuration identity.
            var apply = await RequestAsync(PrivilegedOperationKind.ProxyServerApply, new(Action: null, Material: materialB));
            var identityB = Digest(Encoding.UTF8.GetBytes(SingBoxConfiguration.Render(materialB)));
            var applied = Read<ProxyServerStatus>(apply);
            Check(apply.Success && host.ConfigureCalls == 1 && applied.Running == false && applied.ConfigurationHash == identityB
                && states.Read()!.ConfigurationIdentity == identityB && states.Read()!.Installation.StateRevision == revisionBefore + 1,
                "Publication registers the recorded binary and commits an incremented configuration identity");
            Check(Digest(await File.ReadAllBytesAsync(Path.Combine(root, "config.json.previous"))) == identityA,
                "The replaced configuration is retained for the Server's own rollback");

            // Start applies the published identity and is only reported applied once the process ran it.
            var start = await RequestAsync(PrivilegedOperationKind.ProxyServerAction, new(Action: ProxyServerAction.Start));
            var running = Read<ProxyServerStatus>(start);
            Check(start.Success && running is { Running: true, Applied: true } && running.ConfigurationHash == identityB,
                "Start applies the committed identity and reports it only after the process used it");

            // Publishing while running preserves the run state across the stop/configure/start sequence.
            var republish = await RequestAsync(PrivilegedOperationKind.ProxyServerApply, new(Action: null, Material: materialA));
            var republished = Read<ProxyServerStatus>(republish);
            Check(republish.Success && republished is { Running: true, Applied: true } && republished.ConfigurationHash == identityA
                && host.Stops == 1 && host.Starts == 2 && states.Read()!.ConfigurationIdentity == identityA,
                "Republishing while running stops, replaces, re-registers and restarts the same recorded program");
            Check(Digest(await File.ReadAllBytesAsync(Path.Combine(root, "config.json.previous"))) == identityB,
                "Each publication keeps only the immediately previous configuration");

            // Startup registration is changed without stopping or starting the service.
            var startsBefore = host.Starts;
            var enable = await RequestAsync(PrivilegedOperationKind.ProxyServerAction, new(Action: ProxyServerAction.Enable));
            var enabled = Read<ProxyServerStatus>(enable);
            var disable = await RequestAsync(PrivilegedOperationKind.ProxyServerAction, new(Action: ProxyServerAction.Disable));
            var disabled = Read<ProxyServerStatus>(disable);
            Check(enabled is { BootEnabled: true, Running: true } && disabled is { BootEnabled: false, Running: true }
                && host.Starts == startsBefore,
                "Boot registration changes without restarting the published service");

            // Rollback restores the file this installation replaced and commits its identity.
            var rollback = await RequestAsync(PrivilegedOperationKind.ProxyServerAction, new(Action: ProxyServerAction.Rollback));
            var rolledBack = Read<ProxyServerStatus>(rollback);
            Check(rollback.Success && rolledBack.ConfigurationHash == identityB && states.Read()!.ConfigurationIdentity == identityB
                && Digest(await File.ReadAllBytesAsync(Path.Combine(root, "config.json"))) == identityB,
                "Rollback restores the previous configuration under its recorded identity");

            // On-disk drift is reported and blocks a new publication instead of silently overwriting it.
            await File.AppendAllTextAsync(Path.Combine(root, "config.json"), " ");
            var drifted = Read<ProxyServerStatus>(await RequestAsync(PrivilegedOperationKind.ProxyServerStatus, null));
            var configureBefore = host.ConfigureCalls;
            var blocked = await RequestAsync(PrivilegedOperationKind.ProxyServerApply, new(Action: null, Material: materialA));
            Check(drifted is { Applied: false, ProblemCode: "proxy_server.configuration_drift" }
                && !blocked.Success && blocked.Error == "proxy_server.configuration_drift" && host.ConfigureCalls == configureBefore,
                "Configuration drift is reported and refuses to publish over an unverified file");
            await File.WriteAllBytesAsync(Path.Combine(root, "config.json"), Encoding.UTF8.GetBytes(SingBoxConfiguration.Render(materialB)));

            // A failing registration restores the replaced configuration and the original run state.
            host.FailNextConfigure = true;
            var failed = await RequestAsync(PrivilegedOperationKind.ProxyServerApply, new(Action: null, Material: materialA));
                        var afterFailure = states.Read()!;
            Check(!failed.Success && failed.Error == "proxy_server.config_apply_failed"
                && afterFailure.ConfigurationIdentity == identityB && afterFailure.Pending is null
                && Digest(await File.ReadAllBytesAsync(Path.Combine(root, "config.json"))) == identityB
                && (await host.ObserveAsync(default)).Running,
                "Failed publication compensates to the recorded configuration and restores the running service");

            // A failed stop leaves the publication candidate on disk. Compensation needs its own staging file.
            host.FailNextStop = true;
            var interrupted = await RequestAsync(PrivilegedOperationKind.ProxyServerApply, new(Action: null, Material: materialA));
            var afterStopFailure = await host.ObserveAsync(default);
            Check(!interrupted.Success && interrupted.Error == "proxy_server.config_apply_failed"
                && states.Read()!.ConfigurationIdentity == identityB
                && afterStopFailure.Running && afterStopFailure.AppliedConfigurationIdentity == identityB
                && !File.Exists(Path.Combine(root, "config.json.candidate"))
                && !Directory.EnumerateFiles(root, "*.restore").Any(),
                "A stop failure must restore the old service without colliding with the unpublished candidate");

            // Start can return before the service exits. The old run state still needs compensation.
            host.ExitOnNextStart = true;
            var revisionBeforeExit = states.Read()!.Installation.StateRevision;
            var exited = await RequestAsync(PrivilegedOperationKind.ProxyServerApply, new(Action: null, Material: materialA));
            var restored = await host.ObserveAsync(default);
            Check(!exited.Success && exited.Error == "proxy_server.config_apply_failed"
                && states.Read()!.Installation.StateRevision == revisionBeforeExit
                && states.Read()!.ConfigurationIdentity == identityB
                && restored.Running && restored.AppliedConfigurationIdentity == identityB
                && Digest(await File.ReadAllBytesAsync(Path.Combine(root, "config.json"))) == identityB,
                "A runtime that exits after start cannot commit the candidate and must restore the prior running configuration");

            if (socket is not null)
            {
                var before = states.Read()!.Installation.StateRevision;
                var starts = host.Starts;
                var serverStore = OpenServerStore(root);
                var originalGrant = materialB.Credentials.Single();
                await serverStore.WriteAsync(ProxyServerState.Empty with
                {
                    Published = materialB,
                    Credentials = [new(originalGrant.Id, Guid.NewGuid(), "expired", originalGrant.InboundIds.ToArray(),
                        originalGrant.Value, true, DateTimeOffset.UtcNow.AddMinutes(-1))]
                }, default);
                var expected = materialB with { Credentials = [] };
                var expectedHash = Digest(Encoding.UTF8.GetBytes(SingBoxConfiguration.Render(expected)));
                dropResponse = true;
                try
                {
                    await SendAcrossProcessAsync(socket.SocketPath,
                        null, async child =>
                        {
                            await committed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                            var pending = (await serverStore.ReadAsync(default)).PendingPublication;
                            Check(!child.HasExited && states.Read()!.ConfigurationIdentity == expectedHash
                                && pending is not null && Digest(Encoding.UTF8.GetBytes(SingBoxConfiguration.Render(pending))) == expectedHash,
                                "The Helper must commit the candidate before the client is terminated.");
                            child.Kill();
                            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                            Check(child.ExitCode != 0, "The original client must terminate without a response.");
                        }, root);
                }
                finally { releaseResponse.TrySetResult(); }
                var indexPath = Path.Combine(root, "server", "proxy-server.protected.expiry");
                File.Delete(indexPath);
                var recovery = await SendAcrossProcessAsync(socket.SocketPath, null, expiryRoot: root);
                var reopened = OpenServerStore(root);
                var recoveredState = await reopened.ReadAsync(default);
                Check(recovery.Success && File.Exists(indexPath) && recoveredState.PendingPublication is null
                    && recoveredState.Published?.Credentials.Count == 0,
                    "Server recovery must confirm and clear the original pending publication.");
                var recovered = Read<ProxyServerStatus>(await RequestAsync(PrivilegedOperationKind.ProxyServerStatus, null));
                Check(recovered is { Running: true, Applied: true } && recovered.ConfigurationHash == expectedHash
                    && states.Read()!.Installation.StateRevision == before + 1 && host.Starts == starts + 1,
                    "A new client must observe the committed state without another publication.");
                return count;
            }

            // A tampered program file is never executed again.
            var binary = Path.Combine(directory, binaryName);
            await File.WriteAllBytesAsync(binary, Encoding.UTF8.GetBytes("tampered program"));
            var tampered = await RequestAsync(PrivilegedOperationKind.ProxyServerApply, new(Action: null, Material: materialA));
            var tamperedStart = await RequestAsync(PrivilegedOperationKind.ProxyServerAction, new(Action: ProxyServerAction.Start));
            Check(!tampered.Success && tampered.Error == "proxy_server.runtime_failed"
                && !tamperedStart.Success && tamperedStart.Error == "proxy_server.runtime_failed",
                "Program files that no longer match the recorded artifact are not executed");
            await File.WriteAllBytesAsync(binary, binaryBytes);

            // Uninstall stays with the runtime lifecycle; install is an idempotent no-op for a managed program.
            var uninstall = await RequestAsync(PrivilegedOperationKind.ProxyServerAction, new(Action: ProxyServerAction.Uninstall, KeepData: true));
            var install = await RequestAsync(PrivilegedOperationKind.ProxyServerAction, new(Action: ProxyServerAction.Install));
            Check(!uninstall.Success && uninstall.Error == "proxy_server.managed_runtime_required"
                && install.Success && Read<ProxyServerStatus>(install).Installed,
                "Uninstall requires the runtime lifecycle while Install reports the existing managed program");
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new Exception("Managed service fixture cleanup escaped the temp root");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return count;
    }

    private static ProxyServerMaterial Material(int port)
    {
        var inbound = new ProxyServerInbound(Guid.NewGuid(), "Fixture", ProxyServerProtocol.VlessReality,
            "127.0.0.1", port, "proxy.example.com", port, "proxy.example.com", HandshakeHost: "1.1.1.1", HandshakePort: 443);
        return new(new ProxyServerConfiguration(1, [inbound]),
            [new ProxyServerSecret(Guid.NewGuid(), [inbound.Id], Guid.NewGuid().ToString("D"))],
            [new ProxyServerRealityKey(inbound.Id, Key(), Key(), Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant())]);
    }

    /// <summary>Shaped like a sing-box base64url key; the value is not used by any native program here.</summary>
    private static string Key() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_')[..43];

    private static async Task<PrivilegedOperationResult> SendAcrossProcessAsync(string socketPath, PrivilegedOperationRequest? request, Func<Process, Task>? interrupt = null, string? expiryRoot = null)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName) == "dotnet")
            start.ArgumentList.Add(typeof(ManagedServiceChecks).Assembly.Location);
        start.ArgumentList.Add(expiryRoot is null ? "runtime-managed-service-client" : "runtime-managed-service-expiry-client");
        start.ArgumentList.Add(socketPath);
        if (expiryRoot is not null) start.ArgumentList.Add(expiryRoot);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Unable to start isolated Helper client.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            if (request is not null)
                await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token);
            child.StandardInput.Close();
            if (interrupt is not null)
            {
                await interrupt(child);
                return new(false);
            }
            var response = await child.StandardOutput.ReadToEndAsync(deadline.Token);
            await child.WaitForExitAsync(deadline.Token);
            if (child.ExitCode != 0) throw new InvalidOperationException($"Isolated Helper client exited: {child.ExitCode}.");
            return JsonSerializer.Deserialize<PrivilegedOperationResult>(response)
                ?? throw new InvalidOperationException("Isolated Helper client did not return a result.");
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static UnixSocketPrivilegedOperationTransport CreateTransport(string socketPath) =>
        new(new PrivilegedHelperOptions { SocketPath = socketPath }, NullLogger<UnixSocketPrivilegedOperationTransport>.Instance);

    internal static async Task RunExpiryClientAsync(string socketPath, string root)
    {
        var store = OpenServerStore(root);
        var services = new ServiceCollection();
        services.AddScoped<IProxyServerExpiryExecutor>(_ => new ProxyServerPublicationService(store, CreateTransport(socketPath), new FixtureMode()));
        await using var provider = services.BuildServiceProvider();
        using var worker = new ProxyServerExpiryService(provider.GetRequiredService<IServiceScopeFactory>(), store,
            NullLogger<ProxyServerExpiryService>.Instance);
        await worker.ReconcileAsync(default);
        Console.WriteLine(JsonSerializer.Serialize(new PrivilegedOperationResult(true)));
    }

    private static ProxyServerStore OpenServerStore(string root) => new(new FixturePaths(Path.Combine(root, "server")),
        DataProtectionProvider.Create(Directory.CreateDirectory(Path.Combine(root, "fixture-keys"))));

    private sealed class FixturePaths(string root) : IProxyPlatformPaths
    {
        public string GetStateDirectory() => root;
        public string GetEngineVersionsDirectory(string engineId) => root;
        public string GetEngineDataDirectory(string engineId) => root;
        public string GetProtectedConfigurationDirectory() => root;
        public string GetSanitizedLogDirectory() => root;
    }

    private sealed class FixtureMode : IServerModeResolver
    {
        public RelaxKonOS.Protocol.Common.ServerMode Mode => RelaxKonOS.Protocol.Common.ServerMode.System;
        public bool Supports(ServerHostFeature feature) => feature == ServerHostFeature.Proxy;
        public RelaxKonOS.Protocol.Common.ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    }

    internal static async Task RunSocketClientAsync(string socketPath)
    {
        var request = JsonSerializer.Deserialize<PrivilegedOperationRequest>(await Console.In.ReadLineAsync() ?? "")
            ?? throw new InvalidOperationException("Missing isolated Helper client request.");
        var transport = new UnixSocketPrivilegedOperationTransport(new PrivilegedHelperOptions { SocketPath = socketPath },
            NullLogger<UnixSocketPrivilegedOperationTransport>.Instance);
        var response = await transport.ExecuteAsync(request, default);
        Console.WriteLine(JsonSerializer.Serialize(response));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static LinuxUnixSocketPrivilegedServer OpenSocket(string root, RuntimeArchiveStateStore states,
        ServiceHost host, ISingBoxNativeChecks natives, Func<PrivilegedOperationRequest, PrivilegedOperationResult, Task> beforeResponse)
    {
        var files = new LinuxUnixSocketFileSystem();
        if (files.EffectiveUserId != 0) throw new InvalidOperationException("The isolated Helper socket fixture requires root.");
        var configuration = new LinuxPrivilegedHelperConfiguration(Path.Combine(root, "ipc", "helper.sock"), 0, [0]);
        var socket = new LinuxUnixSocketPrivilegedServer(configuration, async (request, _) =>
        {
            if (request.Operation is not (PrivilegedOperationKind.ProxyServerStatus or PrivilegedOperationKind.ProxyServerValidate
                or PrivilegedOperationKind.ProxyServerApply or PrivilegedOperationKind.ProxyServerAction))
                throw new InvalidOperationException("Unexpected operation on the isolated sing-box socket.");
            var result = await Send(request.Operation, request.ProxyServer, root, states, host, natives);
            await beforeResponse(request, result);
            return result;
        }, files, new LinuxPeerAuthorizer(configuration.AllowedUserIds), Console.WriteLine);
        socket.Start();
        return socket;
    }

    private static async Task<PrivilegedOperationResult> Send(PrivilegedOperationKind operation, ProxyServerHelperRequest? request,
        string root, RuntimeArchiveStateStore states, ServiceHost host, ISingBoxNativeChecks natives)
    {
        using var session = states.Acquire();
        return await SingBoxManagedRuntime.ExecuteAsync(new(operation, ProxyServer: request), root, session, host, default, natives);
    }

    private static T Read<T>(PrivilegedOperationResult result) =>
        JsonSerializer.Deserialize<T>(Convert.FromBase64String(result.OutputBase64!), RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default)!;

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class NativeChecks : ISingBoxNativeChecks
    {
        internal int Calls;
        public Task<string> ValidateAsync(string binary, string configuration, RuntimeRelease release, byte[] bytes,
            CancellationToken ct)
        {
            if (!File.Exists(configuration)) throw new RuntimePolicyException("runtime.native_configuration_invalid");
            Calls++;
            return Task.FromResult(Digest(bytes));
        }
    }

    private sealed class ServiceHost(bool live) : ISingBoxArchiveHost, IAsyncDisposable
    {
        internal SingBoxServiceObservation Observation = new(false, true, false, null, null, null);
        internal int ConfigureCalls;
        internal int Stops;
        internal int Starts;
        internal bool FailNextConfigure;
        internal bool FailNextStop;
        internal bool ExitOnNextStart;
        private Process? process;
        private string? configuration;
        public Task<SingBoxServiceObservation> ObserveAsync(CancellationToken ct) =>
            Task.FromResult(live && process is not { HasExited: false }
                ? Observation with { Running = false, AppliedConfigurationIdentity = null } : Observation);

        public async Task StopAsync(CancellationToken ct)
        {
            Stops++;
            if (FailNextStop)
            {
                FailNextStop = false;
                throw new IOException("Fixture stop failure");
            }
            await DisposeAsync();
            Observation = Observation with { Running = false, AppliedConfigurationIdentity = null };
        }

        public async Task StartAsync(CancellationToken ct)
        {
            Starts++;
            if (live)
            {
                var start = new ProcessStartInfo(Observation.Executable!) { UseShellExecute = false };
                foreach (var arg in new[] { "run", "-c", configuration! }) start.ArgumentList.Add(arg);
                process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start isolated sing-box.");
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(configuration!, ct));
                var port = json.RootElement.GetProperty("inbounds")[0].GetProperty("listen_port").GetInt32();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                while (true)
                {
                    if (process.HasExited) throw new InvalidOperationException($"Isolated sing-box exited: {process.ExitCode}.");
                    using var probe = new TcpClient();
                    try { await probe.ConnectAsync(IPAddress.Loopback, port, deadline.Token); break; }
                    catch (SocketException) { await Task.Delay(50, deadline.Token); }
                }
            }
            if (ExitOnNextStart)
            {
                ExitOnNextStart = false;
                await DisposeAsync();
                return;
            }
            Observation = Observation with { Running = true, AppliedConfigurationIdentity = Current };
        }

        public async ValueTask DisposeAsync()
        {
            if (process is not { } owned) return;
            process = null;
            using (owned)
            {
                if (!owned.HasExited) owned.Kill();
                await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        public Task ConfigureAsync(string binary, string configuration, string identity, bool bootEnabled, CancellationToken ct)
        {
            this.configuration = configuration;
            ConfigureCalls++;
            Current = identity;
            Observation = new(true, true, false, bootEnabled, binary, null);
            if (FailNextConfigure) { FailNextConfigure = false; throw new IOException("Fixture registration failure"); }
            return Task.CompletedTask;
        }
        public Task SetBootAsync(bool enabled, CancellationToken ct)
        {
            Observation = Observation with { BootEnabled = enabled };
            return Task.CompletedTask;
        }
        public Task RemoveOwnedRegistrationAsync(CancellationToken ct)
        {
            Observation = new(false, true, false, null, null, null);
            return Task.CompletedTask;
        }
        public Task<bool> ListenersHealthyAsync(ProxyServerConfiguration published, CancellationToken ct) =>
            Task.FromResult(!live || process is { HasExited: false });
        private string? Current;
    }
}
