using System.Text.Json;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy.Platform;

/// <summary>Runs the real ownership/compensation algorithm against an isolated in-memory OS backend.
/// It never writes the test machine's environment, desktop proxy, or unrelated settings.</summary>
internal sealed class MihomoRemovalHostFixture(string root) : IHostSystemProxyService
{
    private readonly Backend backend = new();
    internal int Calls, Failures, UnavailableFailures;
    internal bool LoseDisableResponse;
    internal Action? AfterDisable;
    internal string? HttpProxy => backend.Values.GetValueOrDefault("http_proxy");
    internal string? HttpsProxy { get => backend.Values.GetValueOrDefault("https_proxy"); set => backend.Values["https_proxy"] = value; }
    internal const string Original = "http://original.invalid:3128";

    internal static MihomoSettingsPublicationFiles Files(IProxyPlatformPaths paths) =>
        new(paths, new MihomoRemovalHostFixture(Path.Combine(paths.GetStateDirectory(), "host-proxy")));

    internal async Task SeedAsync(IProxyPlatformPaths paths, ProxySettingsDto settings)
    {
        Directory.CreateDirectory(paths.GetStateDirectory());
        await File.WriteAllBytesAsync(Path.Combine(paths.GetStateDirectory(), "mihomo-settings.json"), JsonSerializer.SerializeToUtf8Bytes(settings));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(paths.GetStateDirectory(), "mihomo-settings.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (settings.SystemProxyEnabled) await ApplyAsync(settings, settings with { SystemProxyEnabled = false }, false, default);
        Calls = 0;
    }

    public Task<ProxySystemProxyCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(new ProxySystemProxyCapabilities(true, true, false, false));
    public Task<string?> ApplyAsync(ProxySettingsDto settings, ProxySettingsDto previous, bool enforce, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls++;
        if (Failures-- > 0) return Task.FromResult<string?>(ProxyProblemCodes.SystemProxyConflict);
        if (UnavailableFailures-- > 0) return Task.FromResult<string?>(ProxyProblemCodes.PrivilegedOperationUnavailable);
        var options = settings.SystemProxy ?? ProxySystemProxyOptionsDto.Default;
        new LinuxSystemProxyTransaction(Path.Combine(root, "owned-proxy.json"), backend).Apply(new(settings.SystemProxyEnabled,
            settings.SystemProxyHost, settings.MixedPort, options.UseDefaultBypass, options.BypassList, enforce));
        if (!settings.SystemProxyEnabled)
        {
            AfterDisable?.Invoke();
            if (LoseDisableResponse) { LoseDisableResponse = false; throw new IOException("Injected lost host proxy disable response"); }
        }
        return Task.FromResult<string?>(null);
    }

    private sealed class Backend : ILinuxSystemProxyBackend
    {
        internal Dictionary<string, string?> Values { get; } = new(StringComparer.Ordinal)
        { ["http_proxy"] = Original, ["https_proxy"] = Original, ["unrelated"] = "preserve" };
        public IReadOnlyList<LinuxProxyTarget> DesktopTargets() => [];
        public Dictionary<string, string?> Read(LinuxProxyTarget target, IEnumerable<string> keys) =>
            keys.ToDictionary(key => key, key => Values.GetValueOrDefault(key), StringComparer.Ordinal);
        public void Write(LinuxProxyTarget target, IReadOnlyDictionary<string, string?> values)
        { foreach (var pair in values) Values[pair.Key] = pair.Value; }
        public void Notify(LinuxProxyTarget target) { }
    }
}
