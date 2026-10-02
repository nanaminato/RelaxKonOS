internal static class SystemProxyChecks
{
    public static async Task RunAsync()
    {
        var capabilities = new ProxySystemProxyCapabilities(true, false, true, true);
        var wire = JsonSerializer.Serialize(capabilities);
        TestAssert.Assert(wire.Contains("\"supported\":true") && wire.Contains("\"supportsPac\":false"), "Helper capability names must match the public wire contract.");
        try { JsonSerializer.Deserialize<ProxySystemProxyCapabilities>("{\"supported\":true}"); throw new Exception("Missing capability fields were accepted."); }
        catch (JsonException) { }
        using (var fixture = new Fixture())
        {
            var original = await File.ReadAllTextAsync(fixture.Active);
            var current = await fixture.Service.GetAsync(default);
            fixture.Host.Capabilities = new(false, false, false, false);
            TestAssert.Assert(await fixture.Service.UpdateAsync(Request(current with { SystemProxyEnabled = true }), default) == ProxyProblemCodes.NotSupported,
                "Unavailable writer was enabled.");
            fixture.Host.Capabilities = capabilities;
            TestAssert.Assert(await fixture.Service.UpdateAsync(Request(current with { SystemProxyEnabled = true, SystemProxy = ProxySystemProxyOptionsDto.Default with { UsePac = true } }), default)
                == ProxyProblemCodes.NotSupported, "Linux PAC was accepted.");
            TestAssert.Assert(fixture.Host.Calls.Count == 0 && await File.ReadAllTextAsync(fixture.Active) == original, "Capability rejection mutated configuration.");
            fixture.Host.Problem = ProxyProblemCodes.SystemProxyConflict;
            TestAssert.Assert(await fixture.Service.UpdateAsync(Request(current with { SystemProxyEnabled = true, MixedPort = 8123 }), default) == ProxyProblemCodes.SystemProxyConflict,
                "Host conflict was discarded.");
            TestAssert.Assert(await File.ReadAllTextAsync(fixture.Active) == original && !(await fixture.Service.GetAsync(default)).SystemProxyEnabled,
                "Failed host application did not roll back YAML and saved settings.");
            fixture.Host.Problem = ProxyProblemCodes.PrivilegedOperationUnavailable;
            TestAssert.Assert(await fixture.Service.UpdateAsync(Request(current with { MixedPort = 8123 }), default) is null,
                "Absent helper blocked an unrelated setting while the proxy was disabled.");
            TestAssert.Assert(await fixture.Service.ReconcileSystemProxyAsync(true, default) == ProxyProblemCodes.PrivilegedOperationUnavailable,
                "Startup hid unavailable recovery.");
        }
        using (var fixture = new Fixture())
        {
            var current = await fixture.Service.GetAsync(default);
            var original = await File.ReadAllTextAsync(fixture.Active);
            Directory.CreateDirectory(Path.Combine(fixture.Paths.GetStateDirectory(), "mihomo-settings.json")); // Inject persistence failure.
            TestAssert.Assert(await fixture.Service.UpdateAsync(Request(current with { SystemProxyEnabled = true }), default) == ProxyProblemCodes.ConfigApplyFailed,
                "Failed persistence was reported as a successful proxy enable.");
            TestAssert.Assert(fixture.Host.Calls.Count == 2 && !fixture.Host.Calls[1].Settings.SystemProxyEnabled && fixture.Host.Calls[1].Enforce,
                "Persistence failure did not compensate host settings.");
            TestAssert.Assert(await File.ReadAllTextAsync(fixture.Active) == original, "Persistence failure left updated YAML.");
        }
        using (var fixture = new Fixture())
        {
            var current = await fixture.Service.GetAsync(default);
            var enabled = current with { SystemProxyEnabled = true, MixedPort = 8123, SystemProxy = ProxySystemProxyOptionsDto.Default with { GuardEnabled = true } };
            TestAssert.Assert(await fixture.Service.UpdateAsync(Request(enabled), default) is null, "Supported Linux writer was rejected.");
            TestAssert.Assert((await fixture.Service.GetAsync(default)).SystemProxyEnabled, "Applied settings were not saved.");
            await fixture.Service.ReconcileSystemProxyAsync(false, default);
            TestAssert.Assert(fixture.Host.Calls[^1].Enforce, "Configured guard did not enforce the host proxy.");
            var before = fixture.Host.Calls.Count;
            fixture.Host.BlockNext = true;
            var disable = fixture.Service.UpdateAsync(Request(enabled with { SystemProxyEnabled = false }), default);
            await fixture.Host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var guard = fixture.Service.ReconcileSystemProxyAsync(false, default);
            fixture.Host.Release.TrySetResult();
            TestAssert.Assert(await disable is null && await guard is null, "Concurrent disable/guard failed.");
            TestAssert.Assert(fixture.Host.Calls.Count == before + 1 && !fixture.Host.Calls[^1].Settings.SystemProxyEnabled,
                "Stale guard reapplied the proxy after it was disabled.");
            await fixture.Service.ReconcileSystemProxyAsync(true, default);
            TestAssert.Assert(!fixture.Host.Calls[^1].Settings.SystemProxyEnabled, "Startup did not attempt root-side cleanup while disabled.");
        }
        Console.WriteLine("System proxy capability, PAC gating, apply/persist rollback and concurrent guard checks passed.");
    }
    private static UpdateProxySettingsRequest Request(ProxySettingsDto settings) => new(settings.SystemProxyEnabled, settings.AllowLan,
        settings.DnsEnabled, settings.Ipv6Enabled, settings.UnifiedDelay, settings.LogLevel, settings.MixedPort,
        settings.AllowInsecureSubscriptionSources, settings.SystemProxyHost, settings.Tun, settings.SystemProxy);
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "relaxkonos-system-proxy-test-" + Guid.NewGuid().ToString("N"));
        public TestProxyPaths Paths { get; }
        public HostWriter Host { get; } = new();
        public MihomoSettingsService Service { get; }
        public string Active => Path.Combine(Paths.GetProtectedConfigurationDirectory(), "active.yaml");
        public Fixture()
        {
            Paths = new(root);
            Directory.CreateDirectory(Paths.GetProtectedConfigurationDirectory());
            Directory.CreateDirectory(Paths.GetStateDirectory());
            File.WriteAllText(Active, "mixed-port: 7890\nmode: rule\nproxies: []\nproxy-groups: []\nrules: []\n");
            Service = new(Paths, new HealthyMihomoController(), new StaticProxySecretStore(), new MihomoControllerOptions(), Host);
        }
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
    private sealed class HostWriter : IHostSystemProxyService
    {
        public ProxySystemProxyCapabilities Capabilities = new(true, false, true, true);
        public readonly List<(ProxySettingsDto Settings, bool Enforce)> Calls = [];
        public string? Problem;
        public bool BlockNext;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ProxySystemProxyCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken) => Task.FromResult(Capabilities);
        public async Task<string?> ApplyAsync(ProxySettingsDto settings, ProxySettingsDto previous, bool enforce, CancellationToken cancellationToken)
        {
            Calls.Add((settings, enforce));
            if (BlockNext) { BlockNext = false; Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return Problem;
        }
    }
}
