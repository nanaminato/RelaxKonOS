using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy.Platform;

[SupportedOSPlatform("linux")]
internal static class MihomoTunFullServerAcceptance
{
    internal static async Task SeedAsync(string root, MihomoPublishedRuntime material, string identity)
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
        var settings = new ProxySettingsDto(false, false, false, false, false, "info", 18881,
            Tun: new("gvisor", "rk-fd-proof", false, false, false, "", 1500));
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
        if (phase is not ("drain" or "restart")) throw new ArgumentException("Unknown TUN shutdown phase.");
        var first = phase == "drain";
        var assembly = Path.Combine(AppContext.BaseDirectory, "RelaxKonOS.Server.dll");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = budget.Token;
        await using var relay = first ? new ProxyAuthenticatedPublicationChecks.ReplyRelay("/run/rk-tun-reply.sock", socket, ct,
            (operation, _) => operation == "MihomoRuntimeTunStop") : null;
        var start = await ManagedServiceChecks.PrepareFullServerAsync(assembly, relay?.Path ?? socket, root,
            reuse: !first, seedProxyServer: false);
        start.Environment["ASPNETCORE_URLS"] = "http://192.0.2.2:18970";
        start.Environment["Modules__Proxy__Enabled"] = "true";
        start.Environment["Identity__LinuxPamTransport"] = "helper";
        start.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
        start.Environment["Logging__LogLevel__Microsoft.AspNetCore"] = "Warning";
        start.Environment["Logging__LogLevel__Microsoft.EntityFrameworkCore"] = "Warning";
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = ProxyAuthenticatedPublicationChecks.Start(start, Console.Error.WriteLine, line =>
        {
            Console.WriteLine(line);
            if (line.Contains("Application is shutting down", StringComparison.Ordinal)) stopping.TrySetResult();
        });
        try
        {
            await WriteMarkerAsync(root, "full-server-pid", server.Id.ToString());
            var exited = server.WaitForExitAsync(ct);
            if (relay is not null)
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
            if (server.ExitCode != 0) throw new InvalidOperationException($"Full Server exited with code {server.ExitCode}.");
        }
        finally { await ProxyAuthenticatedPublicationChecks.StopAsync(server); }
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
