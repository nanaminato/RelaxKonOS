using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy.Platform;
using RelaxKonOS.Server.Privileged;

[SupportedOSPlatform("linux")]
internal static class MihomoTunFullServerAcceptance
{
    internal static async Task ProbeHelperProfileAsync(string root, string socket, string mode,
        Guid publicationId, Guid executionId, string identity)
    {
        MihomoIpcStartupAcceptance.RequirePrivateHost(rootRequired: false);
        if (root != Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SYSTEMD_WORK") || mode is not ("pam" or "query"))
            throw new InvalidOperationException("The Helper profile requires its private systemd fixture.");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var transport = new UnixSocketPrivilegedOperationTransport(new() { SocketPath = socket },
            NullLogger<UnixSocketPrivilegedOperationTransport>.Instance);
        var password = mode == "pam" ? await Console.In.ReadLineAsync(budget.Token) : null;
        for (var index = 0; index < 6; index++)
        {
            var request = mode == "pam"
                ? new PrivilegedOperationRequest(PrivilegedOperationKind.AuthenticateSystemUser,
                    SystemAuthenticationUsername: identity, SystemAuthenticationPassword: password)
                : new(PrivilegedOperationKind.MihomoRuntimeQuery, OperationId: executionId);
            var result = await transport.ExecuteAsync(request, budget.Token);
            if (!result.Success) throw new InvalidOperationException("The fixture Helper read failed.");
            if (mode == "query" && (result.OutputBase64 is not { } output
                || JsonSerializer.Deserialize<RuntimeInstallationSnapshot>(Convert.FromBase64String(output)) is not
                    { PendingOperationId: null, MihomoTunRunning: true, MihomoTunAction: { Outcome: RuntimeSwitchOutcome.Succeeded } receipt }
                || receipt.OperationId != executionId || receipt.PublicationId != publicationId))
                throw new InvalidOperationException("The read did not match the active named execution.");
        }
    }

    internal static async Task ProbeNamedStartRecoveryAsync(string root, string socket, Guid publicationId, Guid executionId)
    {
        RequirePrivateFixture(root);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var store = new MihomoPublicationStore(new ProxyPlatformPaths(), ManagedServiceChecks.OpenProtection(root));
        var published = (await store.ReadAsync(budget.Token)).Published;
        if (published is null) throw new InvalidOperationException("The fixture requires its published configuration.");
        await using var callback = new RuntimeAuthorizationServer<MihomoCurrentAuthorization>(executionId, _ =>
            Task.FromResult(new MihomoCurrentAuthorization(DateTimeOffset.UtcNow.AddMinutes(1), published)));
        var transport = new UnixSocketPrivilegedOperationTransport(new() { SocketPath = socket },
            NullLogger<UnixSocketPrivilegedOperationTransport>.Instance);
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.MihomoRuntimeTunStart, OperationId: executionId,
            RuntimeLifecycle: new(Authorization: callback.Channel, MihomoTunPublicationId: publicationId)), budget.Token);
        if (result is not { Success: true, OutputBase64: { } output }
            || JsonSerializer.Deserialize<RuntimeSwitchResult>(Convert.FromBase64String(output)) is not
                { Outcome: RuntimeSwitchOutcome.RecoveryRequired, ProblemCode: "runtime.network_dns_sender_unconfirmed" } receipt
            || receipt.OperationId != executionId)
            throw new InvalidOperationException($"The named TUN retry did not retain the sender gate: {result.Error}.");
    }

    internal static async Task ProbeSquidReceiptReplayAsync(string root, string socket, Guid operationId, int port)
    {
        RequirePrivateFixture(root);
        if (port is not (18884 or 18885 or 18886)) throw new ArgumentOutOfRangeException(nameof(port));
        var transport = new UnixSocketPrivilegedOperationTransport(new() { SocketPath = socket },
            NullLogger<UnixSocketPrivilegedOperationTransport>.Instance);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observation = await transport.ExecuteAsync(new(PrivilegedOperationKind.SquidConfigurationReceipt,
            OperationId: operationId), budget.Token);
        var request = new PrivilegedOperationRequest(PrivilegedOperationKind.SquidApplyConfiguration, OperationId: operationId,
            SquidConfiguration: new(new SquidConfiguration { Port = port }));
        var duplicate = await transport.ExecuteAsync(request, budget.Token);
        var conflict = await transport.ExecuteAsync(request with { SquidConfiguration = new(new SquidConfiguration { Port = 18887 }) }, budget.Token);
        if (conflict is not { Success: false, Error: "squid.idempotency_conflict", SquidPublicationReceipt: null })
            throw new InvalidOperationException("The original Squid Helper ID admitted a different candidate.");
        if (port == 18886)
        {
            var fresh = await transport.ExecuteAsync(request with { OperationId = Guid.NewGuid() }, budget.Token);
            if (!Unconfirmed(observation) || !Unconfirmed(duplicate) || !Unconfirmed(fresh))
                throw new InvalidOperationException("An unconfirmed native Squid publication did not retain the Helper gate.");
        }
        else if (observation is not { Success: true, SquidPublicationReceipt: { } receipt }
            || receipt.OperationId != operationId || duplicate.SquidPublicationReceipt != receipt || duplicate.Success != receipt.Applied)
            throw new InvalidOperationException("The original Squid Helper ID did not preserve its receipt.");

        static bool Unconfirmed(PrivilegedOperationResult result) => result is
            { Success: false, ProblemCode: PrivilegedProblemCode.RecoveryRequired, SquidPublicationReceipt: null };
    }

    internal static async Task SeedAsync(string root, MihomoPublishedRuntime material, string identity, bool joint)
    {
        RequirePrivateFixture(root);
        var paths = new ProxyPlatformPaths();
        var protection = ManagedServiceChecks.OpenProtection(root);
        var store = new MihomoPublicationStore(paths, protection);
        Directory.CreateDirectory(paths.GetStateDirectory());
        Directory.CreateDirectory(paths.GetProtectedConfigurationDirectory());
        await File.WriteAllTextAsync(Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml"), material.Yaml);
        var operation = Guid.NewGuid();
        await store.BeginAsync(operation, 0, material, CancellationToken.None);
        await store.ConfirmAsync(operation, identity, CancellationToken.None);
        var settings = new ProxySettingsDto(joint, false, false, false, false, "info", 18881,
            Tun: new("gvisor", "rk-fd-proof", false, false, false, "", 1500),
            SystemProxy: new(GuardEnabled: joint, GuardIntervalSeconds: 5));
        await File.WriteAllBytesAsync(Path.Combine(paths.GetStateDirectory(), "mihomo-settings.json"),
            JsonSerializer.SerializeToUtf8Bytes(settings));
        var secretDirectory = Directory.CreateDirectory(Path.Combine(root, "full-server", "data")).FullName;
        var secretPath = Path.Combine(secretDirectory, "proxy-controller.secret");
        await File.WriteAllTextAsync(secretPath, protection.CreateProtector("RelaxKonOS.Proxy.ControllerSecret.v1")
            .Protect(material.ControllerSecret));
        File.SetUnixFileMode(secretPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var uid = Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SERVER_UID");
        var gid = Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SERVER_GID");
        await NativeRuntimeProcess.RunAsync("/usr/bin/chown",
            ["-R", $"{uid}:{gid}", "--", paths.GetProtectedConfigurationDirectory()], 1024);
    }

    internal static async Task RunAsync(string root, string socket, string phase)
    {
        RequirePrivateFixture(root);
        if (phase is not ("drain" or "cleanup-drain" or "cleanup-cut" or "restart" or "joint-drain" or "joint-restart" or "admission"
            or "admission-managed" or "admission-disabled" or "handoff-disabled" or "handoff-restart"
            or "squid-new" or "squid-cut" or "squid-restart" or "squid-receipt-cut" or "squid-lifecycle-cut" or "squid-account-cut" or "removal-cut" or "zero-dns-publish" or "zero-dns-recover"
            or "named-start-cut" or "stop-reply-drop" or "start-reply-drop" or "baseline-on" or "baseline-off"))
            throw new ArgumentException("Unknown TUN shutdown phase.");
        var joint = phase.StartsWith("joint-", StringComparison.Ordinal);
        var first = phase.EndsWith("drain", StringComparison.Ordinal) || phase == "cleanup-cut";
        var cleanupDrain = phase == "cleanup-drain";
        var cleanupScope = cleanupDrain || phase == "cleanup-cut";
        var dropStopReply = phase == "stop-reply-drop";
        var dropStartReply = phase == "start-reply-drop";
        var handoff = phase == "handoff-disabled";
        var disabled = phase is "admission-disabled" or "baseline-off";
        var assembly = Path.Combine(AppContext.BaseDirectory, "RelaxKonOS.Server.dll");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(handoff ? 90 : joint ? 75
            : phase is "baseline-on" or "baseline-off" ? 65 : 45));
        var ct = budget.Token;
        using var expiry = handoff || joint && first ? new ProxyJointShutdownExpiry(root, socket) : null;
        if (expiry is not null) await expiry.PrepareAsync(ct, handoff ? TimeSpan.FromSeconds(18) : null);
        await using var guard = expiry is not null ? new ProxyAuthenticatedPublicationChecks.ReplyRelay("/run/rk-guard-reply.sock", socket, ct,
            (operation, _) => operation == "LinuxSystemProxyApply"
                && File.ReadLines("/etc/environment").Any(line => line == "http_proxy=http://127.0.0.1:14444"
                    || line == "http_proxy=\"http://127.0.0.1:14444\"")) : null;
        await using var expiryRelay = expiry is not null ? new ProxyAuthenticatedPublicationChecks.ReplyRelay("/run/rk-expiry-reply.sock",
            guard!.Path, ct, (operation, _) => operation == "ProxyServerApply") : null;
        var originalStart = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queryRelay = dropStartReply ? new ProxyAuthenticatedPublicationChecks.ReplyRelay(
            "/run/rk-tun-query.sock", socket, ct, (operation, id) => operation == "MihomoRuntimeQuery"
                && originalStart.Task.IsCompletedSuccessfully && id == originalStart.Task.Result
                && !File.Exists(Path.Combine(root, "full-tun-query-resume"))) : null;
        var tunOperation = dropStartReply ? "MihomoRuntimeTunStart" : "MihomoRuntimeTunStop";
        await using var relay = (first && !cleanupScope) || dropStopReply || dropStartReply ? new ProxyAuthenticatedPublicationChecks.ReplyRelay(
            "/run/rk-tun-reply.sock", queryRelay?.Path ?? expiryRelay?.Path ?? socket, ct,
            (operation, _) => operation == tunOperation) : null;
        var squidOperation = phase switch
        {
            "squid-cut" => "SquidApplyConfiguration",
            "squid-receipt-cut" => "SquidConfigurationReceipt",
            "squid-lifecycle-cut" => "SquidTrackedLifecycle",
            "squid-account-cut" => "SquidAccountMutate",
            _ => null
        };
        await using var squidRelay = squidOperation is not null ? new ProxyAuthenticatedPublicationChecks.ReplyRelay(
            "/run/rk-squid-reply.sock", socket, ct, (operation, _) => operation == squidOperation,
            holdConfirmedSquidRollback: phase == "squid-cut") : null;
        await using var removalRelay = phase == "removal-cut" ? new ProxyAuthenticatedPublicationChecks.ReplyRelay(
            "/run/rk-removal-reply.sock", socket, ct, (operation, _) => operation == "MihomoRuntimeExecute") : null;
        await using var recoveryRelay = phase == "zero-dns-recover" ? new ProxyAuthenticatedPublicationChecks.ReplyRelay(
            "/run/rk-zero-dns-reply.sock", socket, ct, (operation, _) => operation == "MihomoRuntimeRecover") : null;
        var start = await ManagedServiceChecks.PrepareFullServerAsync(assembly,
            relay?.Path ?? expiryRelay?.Path ?? squidRelay?.Path ?? removalRelay?.Path ?? recoveryRelay?.Path ?? socket, root,
            reuse: !first && !dropStopReply && phase is not ("admission" or "squid-new" or "removal-cut" or "zero-dns-publish"), seedProxyServer: false);
        start.Environment["ASPNETCORE_URLS"] = "http://192.0.2.2:18970";
        if (cleanupScope)
        {
            start.Environment["DOTNET_STARTUP_HOOKS"] = typeof(StartupHook).Assembly.Location;
            start.Environment["RELAXKONOS_TEST_TUN_CLEANUP"] = start.WorkingDirectory;
        }
        start.Environment["Modules__Proxy__Enabled"] = disabled ? "false" : "true";
        if (phase is "admission-managed" or "handoff-disabled" or "handoff-restart")
        {
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("Modules__", StringComparison.Ordinal)).ToArray())
                start.Environment.Remove(key);
            start.Environment["ModuleManagement__Authority"] = "managed";
        }
        start.Environment["Identity__LinuxPamTransport"] = "helper";
        if (joint || handoff || phase == "handoff-restart")
            start.Environment["Certificate__StorageRoot"] = Path.Combine(start.WorkingDirectory, "certificate-fixture");
        start.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
        start.Environment["Logging__LogLevel__Microsoft.AspNetCore"] = "Warning";
        start.Environment["Logging__LogLevel__Microsoft.EntityFrameworkCore"] = "Warning";
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startupRefusal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = ProxyAuthenticatedPublicationChecks.Start(start, line =>
        {
            Console.Error.WriteLine(line);
            if (line.Contains("Refusing to start with unresolved durable work", StringComparison.Ordinal))
                startupRefusal.TrySetResult(line);
        }, line =>
        {
            Console.WriteLine(line);
            if (line.Contains("Application is shutting down", StringComparison.Ordinal)) stopping.TrySetResult();
        });
        try
        {
            await WriteMarkerAsync(root, "full-server-pid", server.Id.ToString());
            var exited = server.WaitForExitAsync(ct);
            var committedReply = squidRelay ?? removalRelay ?? recoveryRelay;
            if (committedReply is not null)
            {
                if (await Task.WhenAny(committedReply.Committed.Task, exited) == exited)
                    throw new InvalidOperationException("Full Server stopped before the original response reached the relay.");
                var original = await committedReply.Committed.Task.WaitAsync(ct);
                var marker = recoveryRelay is not null ? "full-zero-dns-recovery-held"
                    : removalRelay is not null ? "full-removal-response-held"
                    : phase == "squid-receipt-cut" ? "full-squid-receipt-held" : "full-squid-response-held";
                await WriteMarkerAsync(root, marker, original.ToString("N"));
                await exited;
                if (server.ExitCode == 137) return;
                throw new InvalidOperationException($"Expected SIGKILL at the original response boundary, got {server.ExitCode}.");
            }
            if (expiry is not null)
            {
                var guardDrain = DrainAsync(guard!, "guard");
                var tunDrain = relay is null ? Task.CompletedTask : DrainAsync(relay, "tun");
                var expiryDrain = DrainExpiryAsync(expiryRelay!, expiry);
                if (handoff)
                {
                    await WaitForMarkerAsync("full-handoff-ready");
                    await expiry.VerifyLiveBeforeExpiryAsync(ct);
                    await WriteMarkerAsync(root, "full-expiry-live", "forwarded");
                }
                await stopping.Task.WaitAsync(ct);
                await WriteMarkerAsync(root, "full-server-stopping", "stopping");
                await Task.WhenAll(guardDrain, tunDrain, expiryDrain);
                await exited;
                if (server.ExitCode != 0) throw new InvalidOperationException($"Full Server exited with code {server.ExitCode}.");
                await WriteMarkerAsync(root, "full-server-exited", "0");
                var (receipt, pid) = await expiryDrain;
                await WaitForMarkerAsync("full-joint-finish");
                await expiry.VerifyTerminalAsync(receipt, pid, ct);
            }
            else if (relay is not null)
            {
                if (await Task.WhenAny(relay.Committed.Task, exited) == exited)
                    throw new InvalidOperationException("Full Server stopped before the named TUN response reached the relay.");
                var original = await relay.Committed.Task.WaitAsync(ct);
                await WriteMarkerAsync(root, "full-tun-response-held", original.ToString("N"));
                if (dropStopReply || dropStartReply)
                {
                    await WaitForMarkerAsync("full-tun-response-drop");
                    if (queryRelay is not null) originalStart.SetResult(original);
                    relay.DropHeldResponse();
                    if (queryRelay is not null)
                    {
                        var query = await queryRelay.Committed.Task.WaitAsync(ct);
                        await WriteMarkerAsync(root, "full-tun-query-response-held", query.ToString("N"));
                        await WaitForMarkerAsync("full-tun-query-response-drop");
                        queryRelay.DropHeldResponse();
                    }
                }
                else
                {
                    await stopping.Task.WaitAsync(ct);
                    await WriteMarkerAsync(root, "full-server-stopping", original.ToString("N"));
                    await WaitForMarkerAsync("full-tun-response-release");
                    relay.ReleaseHeldResponse();
                }
            }
            if (cleanupDrain)
            {
                await stopping.Task.WaitAsync(ct);
                await WriteMarkerAsync(root, "full-server-stopping", "cleanup");
            }
            await exited;
            if (phase is ("zero-dns-publish" or "named-start-cut" or "cleanup-cut") && server.ExitCode == 137) return;
            if (disabled && server.ExitCode != 0 && startupRefusal.Task.IsCompletedSuccessfully)
            {
                await WriteMarkerAsync(root, "full-startup-refused", await startupRefusal.Task);
                return;
            }
            if (server.ExitCode != 0) throw new InvalidOperationException($"Full Server exited with code {server.ExitCode}.");
        }
        finally { await ProxyAuthenticatedPublicationChecks.StopAsync(server); }

        async Task DrainAsync(ProxyAuthenticatedPublicationChecks.ReplyRelay barrier, string domain)
        {
            var original = await barrier.Committed.Task.WaitAsync(ct);
            await WriteMarkerAsync(root, $"full-{domain}-response-held", original.ToString("N"));
            await WaitForMarkerAsync($"full-{domain}-response-release");
            barrier.ReleaseHeldResponse();
        }

        async Task<(ProxyServerPublicationReceipt Receipt, string Pid)> DrainExpiryAsync(
            ProxyAuthenticatedPublicationChecks.ReplyRelay barrier, ProxyJointShutdownExpiry probe)
        {
            var original = await barrier.Committed.Task.WaitAsync(ct);
            var observed = await probe.ObserveAsync(original, ct);
            await WriteMarkerAsync(root, "full-expiry-response-held", original.ToString("N"));
            await WaitForMarkerAsync("full-expiry-response-release");
            barrier.ReleaseHeldResponse();
            return observed;
        }

        async Task WaitForMarkerAsync(string name)
        {
            while (!File.Exists(Path.Combine(root, name))) await Task.Delay(25, ct);
        }
    }

    private static void RequirePrivateFixture(string root)
    {
        MihomoIpcStartupAcceptance.RequirePrivateHost();
        if (root != Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SYSTEMD_WORK"))
            throw new InvalidOperationException("Full TUN acceptance requires the isolated systemd work directory.");
    }

    private static async Task WriteMarkerAsync(string root, string name, string value)
    {
        var path = Path.Combine(root, name);
        await File.WriteAllTextAsync(path + ".new", value);
        File.Move(path + ".new", path, overwrite: true);
    }
}
