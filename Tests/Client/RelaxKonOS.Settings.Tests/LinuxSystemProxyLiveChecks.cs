extern alias Server;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.Protocol.Proxy;
using Server::RelaxKonOS.Server.Privileged;
using Server::RelaxKonOS.Server.Proxy;
using Server::RelaxKonOS.Server.Proxy.Mihomo;
using Server::RelaxKonOS.Server.Proxy.Platform;
using System.Diagnostics;
using System.Reflection;
using RelaxKonOS.PrivilegedHelper;
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

    internal static async Task RunAsync(string hostMountNamespace, string hostNetworkNamespace, bool worker)
    {
        RequireIsolation(hostMountNamespace, hostNetworkNamespace);
        if (worker)
        {
            await ApplyAsync(Desired);
            Console.WriteLine("PROXY_APPLIED");
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return;
        }

        if (File.ReadAllText(EnvironmentFile) != Original || File.Exists(Journal))
            throw new InvalidOperationException("Expected a fresh isolated proxy environment.");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true };
        if (Path.GetFileNameWithoutExtension(start.FileName) == "dotnet")
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var argument in new[] { "--linux-system-proxy-live-worker", hostMountNamespace, hostNetworkNamespace })
            start.ArgumentList.Add(argument);
        using (var original = Process.Start(start) ?? throw new InvalidOperationException("Unable to start proxy worker."))
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
        await VerifyGuardAsync();
        Console.WriteLine("PASS LINUX SYSTEM PROXY LIVE: process killed, original journal restored once, external edit preserved, guard no-op verified over independent Helper IPC");
    }

    private static async Task VerifyGuardAsync()
    {
        var paths = new FixturePaths();
        var reader = new GuardSettings();
        var transport = new CountingTransport(CreateTransport());
        var host = new HostSystemProxyService(transport);
        var reconciler = new MihomoSystemProxyReconciler(paths, reader, new UnusedControllerSecret(), host,
            new MihomoPublicationStore(paths, new EphemeralDataProtectionProvider()));
        var registrations = new ServiceCollection();
        registrations.AddSingleton<IProxySystemProxyReconciler>(reconciler);
        await using var services = registrations.BuildServiceProvider();
        using var guard = new SystemProxyGuardHostedService(reader, services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SystemProxyGuardHostedService>.Instance);

        await guard.StartAsync(default);
        try
        {
            await transport.Applied.Reader.ReadAsync().AsTask().WaitAsync(Budget);
            var firstWrite = File.GetLastWriteTimeUtc(Journal);
            var original = File.ReadAllBytes(Journal);
            await transport.Applied.Reader.ReadAsync().AsTask().WaitAsync(Budget);
            if (File.GetLastWriteTimeUtc(Journal) != firstWrite || !File.ReadAllBytes(Journal).SequenceEqual(original))
                throw new InvalidOperationException("An unchanged guard pass rewrote its ownership journal.");

            const string competing = "http://127.0.0.1:3456";
            WriteExternalProxy(competing);
            await transport.Applied.Reader.ReadAsync().AsTask().WaitAsync(Budget);
            var guarded = LinuxEnvironmentDocument.Parse(File.ReadAllText(EnvironmentFile)).Values;
            if (guarded.GetValueOrDefault("http_proxy") != "http://127.0.0.1:17890")
                throw new InvalidOperationException("The enabled guard did not restore its configured proxy.");
        }
        finally { await guard.StopAsync(default).WaitAsync(Budget); }

        WriteExternalProxy("http://127.0.0.1:4567");
        reader.Enabled = false;
        if (await reconciler.ReconcileSystemProxyAsync(startup: true, default) is not null
            || File.Exists(Journal) || LinuxEnvironmentDocument.Parse(File.ReadAllText(EnvironmentFile)).Values.GetValueOrDefault("http_proxy")
                != "http://127.0.0.1:4567")
            throw new InvalidOperationException("Startup teardown must retain the newer external proxy setting.");
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

        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Operation != PrivilegedOperationKind.LinuxSystemProxyApply)
                throw new InvalidOperationException("Unexpected isolated guard operation.");
            var result = await inner.ExecuteAsync(request, cancellationToken);
            if (!result.Success) throw new InvalidOperationException($"Isolated guard apply failed: {result.ProblemCode}");
            Applied.Writer.TryWrite(true);
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
        public string GetStateDirectory() => "/var/lib/relaxkonos-system-proxy/guard-state";
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
