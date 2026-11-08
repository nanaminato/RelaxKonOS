using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy.Platform;

[SupportedOSPlatform("linux")]
internal static class MihomoTunFullServerAcceptance
{
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
        if (phase is not ("drain" or "restart" or "joint-drain" or "joint-restart" or "admission"
            or "admission-managed" or "admission-disabled"))
            throw new ArgumentException("Unknown TUN shutdown phase.");
        var joint = phase.StartsWith("joint-", StringComparison.Ordinal);
        var first = phase.EndsWith("drain", StringComparison.Ordinal);
        var disabled = phase == "admission-disabled";
        var assembly = Path.Combine(AppContext.BaseDirectory, "RelaxKonOS.Server.dll");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(joint ? 75 : 45));
        var ct = budget.Token;
        using var expiry = joint && first ? new ProxyJointShutdownExpiry(root, socket) : null;
        if (expiry is not null) await expiry.PrepareAsync(ct);
        await using var guard = joint && first ? new ProxyAuthenticatedPublicationChecks.ReplyRelay("/run/rk-guard-reply.sock", socket, ct,
            (operation, _) => operation == "LinuxSystemProxyApply"
                && File.ReadLines("/etc/environment").Any(line => line == "http_proxy=http://127.0.0.1:14444"
                    || line == "http_proxy=\"http://127.0.0.1:14444\"")) : null;
        await using var expiryRelay = joint && first ? new ProxyAuthenticatedPublicationChecks.ReplyRelay("/run/rk-expiry-reply.sock",
            guard!.Path, ct, (operation, _) => operation == "ProxyServerApply") : null;
        await using var relay = first ? new ProxyAuthenticatedPublicationChecks.ReplyRelay("/run/rk-tun-reply.sock", expiryRelay?.Path ?? socket, ct,
            (operation, _) => operation == "MihomoRuntimeTunStop") : null;
        var start = await ManagedServiceChecks.PrepareFullServerAsync(assembly, relay?.Path ?? socket, root,
            reuse: !first && phase != "admission", seedProxyServer: false);
        start.Environment["ASPNETCORE_URLS"] = "http://192.0.2.2:18970";
        start.Environment["Modules__Proxy__Enabled"] = disabled ? "false" : "true";
        if (phase == "admission-managed")
        {
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("Modules__", StringComparison.Ordinal)).ToArray())
                start.Environment.Remove(key);
            start.Environment["ModuleManagement__Authority"] = "managed";
        }
        start.Environment["Identity__LinuxPamTransport"] = "helper";
        if (joint) start.Environment["Certificate__StorageRoot"] = Path.Combine(start.WorkingDirectory, "certificate-fixture");
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
            if (expiry is not null)
            {
                var guardDrain = DrainAsync(guard!, "guard");
                var tunDrain = DrainAsync(relay!, "tun");
                var expiryDrain = DrainExpiryAsync(expiryRelay!, expiry);
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
                await stopping.Task.WaitAsync(ct);
                await WriteMarkerAsync(root, "full-server-stopping", original.ToString("N"));
                while (!File.Exists(Path.Combine(root, "full-tun-response-release"))) await Task.Delay(25, ct);
                relay.ReleaseHeldResponse();
            }
            await exited;
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
