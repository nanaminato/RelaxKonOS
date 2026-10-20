using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;


namespace RelaxKonOS.PrivilegedHelper;

/// <summary>One fixed systemd scope. Registration metadata alone never proves a running configuration.</summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxMihomoArchiveHost : IMihomoArchiveHost
{
    internal const string Root = "/var/lib/relaxkonos/managed-mihomo";
    internal const string Unit = "relaxkonos-mihomo.service";
    private const string UnitPath = "/etc/systemd/system/" + Unit;
    private const string Credential = "/run/credentials/" + Unit + "/active.yaml";
    private const string RegistrationPath = Root + "/archive-service.json";
    private sealed record Registration(string Binary, string Data, string BinaryHash, string ConfigurationHash, string Version);
    private sealed record Verified(int Pid, DateTime StartedAt, string Identity);

    internal static void ValidatePaths(string binary, string data, string configuration, string identity)
    {
        if (!Path.IsPathFullyQualified(binary) || configuration != Root + "/active.yaml"
            || identity.Length != 64 || !identity.All(Uri.IsHexDigit)) Fail("service_scope_invalid");
        var parts = Path.GetRelativePath(Root + "/versions", Path.GetFullPath(binary)).Split('/');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var id) || id == Guid.Empty
            || !Protocol.ManagedRuntimes.RuntimeArchiveRules.SafePath(string.Join('/', parts.Skip(1)))
            || Path.GetFileName(binary) != "mihomo" || Path.GetFullPath(binary) != binary
            || data != Root + "/data/" + parts[0]) Fail("service_scope_invalid");
        // Unit syntax is intentionally more restrictive than the archive filename grammar.
        if (binary.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('/' or '.' or '-' or '_'))) Fail("service_scope_invalid");
    }

    private static string UnitText(Registration registration) =>
        $"# RelaxKonOS managed archive Mihomo\n[Unit]\nDescription=RelaxKonOS Mihomo\nAfter=network-online.target\n[Service]\nType=simple\nUser=root\nLoadCredential=active.yaml:{Root}/active.yaml\nExecStart={registration.Binary} -d {registration.Data} -f %d/active.yaml\nRestart=on-failure\nNoNewPrivileges=yes\nPrivateTmp=yes\nPrivateDevices=yes\nProtectSystem=strict\nProtectHome=yes\nInaccessiblePaths=-/etc/relaxkonos -/opt/relaxkonos\nReadWritePaths={registration.Data}\nAmbientCapabilities=CAP_NET_BIND_SERVICE\nCapabilityBoundingSet=CAP_NET_BIND_SERVICE\nRestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_NETLINK\n[Install]\nWantedBy=multi-user.target\n";

    private static async Task RequireProtectedAsync(string path)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        for (string? entry = path; entry is not null; entry = Path.GetDirectoryName(entry))
        {
            var fields = (await RunAsync("/usr/bin/stat", ["-c", "%u %a", "--", entry])).Trim().Split(' ');
            if (fields.Length != 2 || fields[0] != "0" || (Convert.ToInt32(fields[1], 8) & 0x12) != 0) Fail("service_scope_invalid");
        }
    }

    private static async Task<Registration?> ReadRegistrationAsync(CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(UnitPath);
        RuntimeSourceStore.RequireNoLinks(RegistrationPath);
        if (!File.Exists(UnitPath) && !File.Exists(RegistrationPath)) return null;
        if (!File.Exists(UnitPath) || !File.Exists(RegistrationPath)) Fail("external_installation");
        await RequireProtectedAsync(UnitPath);
        await RequireProtectedAsync(RegistrationPath);
        if (new FileInfo(RegistrationPath).Length > 8192 || new FileInfo(UnitPath).Length > 8192) Fail("service_scope_invalid");
        var registration = JsonSerializer.Deserialize<Registration>(await File.ReadAllTextAsync(RegistrationPath, ct));
        if (registration is null) Fail("state_unknown");
        ValidatePaths(registration.Binary, registration.Data, Root + "/active.yaml", registration.ConfigurationHash);
        if (registration.BinaryHash.Length != 64 || !registration.BinaryHash.All(Uri.IsHexDigit)
            || MihomoRuntimeSupport.CapabilityFor(registration.Version) is null
            || await File.ReadAllTextAsync(UnitPath, ct) != UnitText(registration)) Fail("external_installation");
        return registration;
    }

    private static async Task<Dictionary<string, string>> StateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var output = await RunAsync("/usr/bin/systemctl", ["show", Unit,
            "--property=LoadState,ActiveState,SubState,MainPID,UnitFileState,FragmentPath,DropInPaths,NeedDaemonReload,Result"]);
        var state = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts.Length == 2 ? parts[1] : "");
        if (state.GetValueOrDefault("LoadState") != "loaded" || state.GetValueOrDefault("FragmentPath") != UnitPath
            || state.GetValueOrDefault("DropInPaths") != "" || state.GetValueOrDefault("NeedDaemonReload") != "no") Fail("state_unknown");
        return state;
    }

    private static async Task<string> DigestAsync(string path, CancellationToken ct)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
    }

    private static async Task<Verified?> VerifyProcessAsync(Registration registration, Dictionary<string, string> state, CancellationToken ct)
    {
        if (state.GetValueOrDefault("ActiveState") == "inactive" && state.GetValueOrDefault("MainPID") == "0") return null;
        if (state.GetValueOrDefault("ActiveState") != "active" || state.GetValueOrDefault("SubState") != "running"
            || !int.TryParse(state.GetValueOrDefault("MainPID"), out var pid) || pid <= 1) Fail("state_unknown");
        var processId = int.Parse(state["MainPID"], System.Globalization.CultureInfo.InvariantCulture);
        using var process = Process.GetProcessById(processId);
        var started = process.StartTime;
        var proc = $"/proc/{processId}";
        var executable = new FileInfo(proc + "/exe").LinkTarget;
        if (executable != registration.Binary) Fail("state_unknown");
        var arguments = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(proc + "/cmdline", ct)).Split('\0');
        if (!arguments.SequenceEqual(new[] { registration.Binary, "-d", registration.Data, "-f", Credential, "" })) Fail("state_unknown");
        await RequireProtectedAsync(registration.Binary);
        if (!string.Equals(await DigestAsync(proc + "/exe", ct), registration.BinaryHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(await DigestAsync(proc + "/root" + Credential, ct), registration.ConfigurationHash, StringComparison.OrdinalIgnoreCase)) Fail("configuration_drift");
        using var after = Process.GetProcessById(processId);
        if (after.StartTime != started) Fail("state_unknown");
        return new(processId, started, registration.ConfigurationHash);
    }

    public async Task<MihomoServiceObservation> ObserveAsync(CancellationToken ct)
    {
        var registration = await ReadRegistrationAsync(ct);
        if (registration is null)
        {
            // A vendor unit or transient service is still an external installation.
            var load = (await RunAsync("/usr/bin/systemctl", ["show", Unit, "--property=LoadState", "--value"])).Trim();
            if (load != "not-found") Fail("external_installation");
            return new(false, true, false, false, null, null, null);
        }
        var state = await StateAsync(ct);
        // Stopped services have no process proof. Their program, configuration and writable data scope
        // still need the same ownership/write protection checked before start.
        await RequireProtectedAsync(registration.Binary);
        await RequireProtectedAsync(Root + "/active.yaml");
        await RequireProtectedAsync(registration.Data);
        bool? boot = state.GetValueOrDefault("UnitFileState") switch { "enabled" => true, "disabled" => false, _ => null };
        var process = await VerifyProcessAsync(registration, state, ct);
        return new(true, true, process is not null, boot, registration.Binary, process?.Identity, registration.Data);
    }

    public async Task<RuntimeServiceObservation> ObserveRegistrationAsync(CancellationToken ct)
    {
        if (await ReadRegistrationAsync(ct) is null)
        {
            var absent = await ObserveAsync(ct); // Reject an external unit even without our registration.
            return new(absent.Exists, absent.Running, absent.BootEnabled);
        }
        var state = await StateAsync(ct);
        bool? boot = state.GetValueOrDefault("UnitFileState") switch { "enabled" => true, "disabled" => false, _ => null };
        // Anything except a settled zero-PID unit is not proven stopped. No process/image proof is claimed.
        var running = state.GetValueOrDefault("MainPID") != "0" || state.GetValueOrDefault("ActiveState") is not ("inactive" or "failed");
        return new(true, running, boot);
    }

    public async Task ConfigureAsync(string verifiedBinary, string protectedDataDirectory, string protectedConfiguration,
        string configurationIdentity, bool bootEnabled, CancellationToken ct)
    {
        ValidatePaths(verifiedBinary, protectedDataDirectory, protectedConfiguration, configurationIdentity);
        var previous = await ReadRegistrationAsync(ct);
        if (previous is null) await ObserveAsync(ct);
        else if ((await StateAsync(ct)).GetValueOrDefault("MainPID") != "0") Fail("state_unknown");
        await RequireProtectedAsync(verifiedBinary); await RequireProtectedAsync(protectedConfiguration);
        await RequireProtectedAsync(protectedDataDirectory);
        if (!string.Equals(await DigestAsync(protectedConfiguration, ct), configurationIdentity, StringComparison.OrdinalIgnoreCase)) Fail("configuration_drift");
        var report = await MihomoOperations.VersionAsync(verifiedBinary);
        var tag = report.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(2) ?? "";
        var version = MihomoNativeValidation.Parse(report, tag, "linux", MihomoNativeValidation.ArchitectureName).Version;
        if (MihomoRuntimeSupport.CapabilityFor(version) is null) Fail("configuration_unsupported");
        var registration = new Registration(verifiedBinary, protectedDataDirectory, await DigestAsync(verifiedBinary, ct), configurationIdentity, version);
        await WriteAsync(RegistrationPath, JsonSerializer.Serialize(registration), UnixFileMode.UserRead | UnixFileMode.UserWrite, ct);
        await WriteAsync(UnitPath, UnitText(registration), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, ct);
        await CommandAsync("daemon-reload", ct, false);
        // A removed/re-created stopped unit can retain systemd's previous failed state. Registration is
        // verified and MainPID is zero here; do not inherit a failure latch from the previous registration.
        if ((await StateAsync(ct)).GetValueOrDefault("ActiveState") == "failed") await CommandAsync("reset-failed", ct);
        await CommandAsync(bootEnabled ? "enable" : "disable", ct);
    }

    public async Task SetBootAsync(bool enabled, CancellationToken ct)
    {
        if (await ReadRegistrationAsync(ct) is null) throw new RuntimePolicyException("runtime.state_unknown");
        await StateAsync(ct);
        await CommandAsync(enabled ? "enable" : "disable", ct);
        var actual = (await StateAsync(ct)).GetValueOrDefault("UnitFileState") switch
        { "enabled" => true, "disabled" => false, _ => (bool?)null };
        if (actual != enabled) Fail("state_unknown");
    }

    private static async Task WriteAsync(string path, string text, UnixFileMode mode, CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(path); RuntimeSourceStore.RequireNoLinks(path + ".new");
        await using (var output = new FileStream(path + ".new", new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = mode }))
        { await output.WriteAsync(Encoding.UTF8.GetBytes(text), ct); output.Flush(true); }
        File.Move(path + ".new", path, true);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (await ReadRegistrationAsync(ct) is not null)
        {
            await StateAsync(ct); await CommandAsync("stop", ct);
            // systemctl returns while the unit may still be deactivating; the next observation must not
            // mistake that transient state for an unknown one.
            await WaitAsync(state => state.GetValueOrDefault("ActiveState") is "inactive" or "failed", ct);
            // Stopping a failed owned unit leaves systemd's failure latch set. Clear that latch only after
            // the owned process has settled, so recovery can observe an intentionally stopped service.
            if ((await StateAsync(ct)).GetValueOrDefault("ActiveState") == "failed")
            {
                await CommandAsync("reset-failed", ct);
                await WaitAsync(state => state.GetValueOrDefault("ActiveState") == "inactive", ct);
            }
        }
        else await ObserveAsync(ct);
    }
    public async Task StartAsync(CancellationToken ct)
    {
        var registration = await ReadRegistrationAsync(ct) ?? throw new RuntimePolicyException("runtime.state_unknown");
        await StateAsync(ct); await RequireProtectedAsync(registration.Binary); await RequireProtectedAsync(Root + "/active.yaml");
        await RequireProtectedAsync(registration.Data);
        if (!string.Equals(await DigestAsync(registration.Binary, ct), registration.BinaryHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(await DigestAsync(Root + "/active.yaml", ct), registration.ConfigurationHash, StringComparison.OrdinalIgnoreCase)) Fail("configuration_drift");
        // This is an explicit, verified management start, including compensation. Clear a prior failed
        // owned unit, and retry exactly once only for systemd's measured start-limit-hit result. Inactive
        // disabled units can be garbage-collected, so reset-failed must not be sent unconditionally.
        if ((await StateAsync(ct)).GetValueOrDefault("ActiveState") == "failed") await CommandAsync("reset-failed", ct);
        try { await CommandAsync("start", ct); }
        catch (InvalidOperationException)
        {
            var failed = await StateAsync(ct);
            if (failed.GetValueOrDefault("ActiveState") != "failed" || failed.GetValueOrDefault("Result") != "start-limit-hit") throw;
            await CommandAsync("reset-failed", ct);
            await CommandAsync("start", ct);
        }
        // A simple unit is briefly "activating" after systemctl start returns. Observing that window would
        // report state_unknown for a service that is starting exactly as configured.
        await WaitAsync(state => state.GetValueOrDefault("ActiveState") is "inactive" or "failed"
            || state.GetValueOrDefault("ActiveState") == "active" && state.GetValueOrDefault("SubState") == "running", ct);
        await ObserveAsync(ct);
    }

    private static async Task WaitAsync(Func<Dictionary<string, string>, bool> settled, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (settled(await StateAsync(ct))) return;
            await Task.Delay(100, ct);
        }
    }
    public async Task RemoveOwnedRegistrationAsync(CancellationToken ct)
    {
        if (await ReadRegistrationAsync(ct) is null) { await ObserveAsync(ct); return; }
        await StateAsync(ct); await CommandAsync("disable", ct); await CommandAsync("stop", ct);
        File.Delete(UnitPath); File.Delete(RegistrationPath); await CommandAsync("daemon-reload", ct, false);
    }
    public async Task<bool> ListenersHealthyAsync(MihomoPublishedRuntime published, CancellationToken ct)
    {
        var registration = await ReadRegistrationAsync(ct);
        if (registration is null) return false;
        var before = await VerifyProcessAsync(registration, await StateAsync(ct), ct);
        if (before is null) return false;
        var listeners = LinuxOwnedListeners.Read(before.Pid, before.StartedAt);
        if (published.Listeners is not { Count: > 0 } expected || expected.Count > 128
            || published.ControllerPort is < 1 or > 65535 || published.ControllerSecret is not { Length: >= 16 and <= 512 } secret
            || secret.Any(char.IsControl)) return false;
        var healthy = expected.All(i => listeners.Any(e => e.Udp == i.Udp && e.Port == i.Port
            && (i.Address == "*" ? e.Address.Equals(IPAddress.Any) || e.Address.Equals(IPAddress.IPv6Any)
                : IPAddress.TryParse(i.Address, out var address) && e.Address.Equals(address))))
            && listeners.Any(e => !e.Udp && e.Port == published.ControllerPort && e.Address.Equals(IPAddress.Loopback));
        if (healthy) healthy = await ControllerHealthyAsync(published.ControllerPort, secret, registration.Version, ct);
        var after = await VerifyProcessAsync(registration, await StateAsync(ct), ct);
        return healthy && before == after;
    }
    private static async Task CommandAsync(string verb, CancellationToken ct, bool unit = true)
    { ct.ThrowIfCancellationRequested(); await RunAsync("/usr/bin/systemctl", unit ? [verb, Unit] : [verb]); }
    private static Task<string> RunAsync(string binary, IReadOnlyList<string> arguments) =>
        NativeRuntimeProcess.RunAsync(binary, arguments, 65536);

    private static async Task<bool> ControllerHealthyAsync(int port, string secret, string version, CancellationToken ct)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            ct = deadline.Token;
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(3) }) { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/version");
            request.Headers.Authorization = new("Bearer", secret);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 8192) return false;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            var bytes = new byte[8193];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await input.ReadAsync(bytes.AsMemory(length), ct);
                if (read == 0) break;
                length += read;
            }
            if (length > 8192) return false;
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length));
            return document.RootElement.TryGetProperty("version", out var measured)
                && measured.GetString() == "v" + version;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException
            or InvalidOperationException or FormatException) { return false; }
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string code) => throw new RuntimePolicyException("runtime." + code);
}
