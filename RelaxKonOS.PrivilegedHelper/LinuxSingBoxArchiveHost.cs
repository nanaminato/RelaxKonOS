using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ProxyServer;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>One fixed systemd scope. Registration metadata alone never proves a running configuration.</summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxSingBoxArchiveHost : ISingBoxArchiveHost
{
    internal const string Root = "/var/lib/relaxkonos/proxy-server";
    internal const string Unit = "relaxkonos-sing-box.service";
    private const string UnitPath = "/etc/systemd/system/" + Unit;
    private const string Credential = "/run/credentials/" + Unit + "/config.json";
    private const string RegistrationPath = Root + "/archive-service.json";
    private sealed record Registration(string Binary, string BinaryHash, string ConfigurationHash);
    private sealed record Verified(int Pid, DateTime StartedAt, string Identity);

    internal static void ValidatePaths(string binary, string configuration, string identity)
    {
        if (!Path.IsPathFullyQualified(binary) || configuration != Root + "/config.json"
            || identity.Length != 64 || !identity.All(Uri.IsHexDigit)) Fail("service_scope_invalid");
        var parts = Path.GetRelativePath(Root + "/versions", Path.GetFullPath(binary)).Split('/');
        if (parts.Length < 2 || !Guid.TryParseExact(parts[0], "N", out var id) || id == Guid.Empty
            || !Protocol.ManagedRuntimes.RuntimeArchiveRules.SafePath(string.Join('/', parts.Skip(1)))
            || Path.GetFileName(binary) != "sing-box" || Path.GetFullPath(binary) != binary) Fail("service_scope_invalid");
        // Unit syntax is intentionally more restrictive than the archive filename grammar.
        if (binary.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('/' or '.' or '-' or '_'))) Fail("service_scope_invalid");
    }

    private static string UnitText(Registration registration) =>
        $"# RelaxKonOS managed archive sing-box\n[Unit]\nDescription=RelaxKonOS sing-box proxy server\nAfter=network-online.target\n[Service]\nType=simple\nDynamicUser=yes\nLoadCredential=config.json:{Root}/config.json\nExecStart={registration.Binary} run -c %d/config.json\nRestart=on-failure\nNoNewPrivileges=yes\nPrivateTmp=yes\nProtectSystem=strict\nProtectHome=yes\nAmbientCapabilities=CAP_NET_BIND_SERVICE\nCapabilityBoundingSet=CAP_NET_BIND_SERVICE\nRestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_NETLINK\n[Install]\nWantedBy=multi-user.target\n";

    private static async Task RequireProtectedAsync(string path)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        for (string? entry = path; entry is not null; entry = Path.GetDirectoryName(entry))
        {
            var fields = (await SingBoxOperations.RunAsync("/usr/bin/stat", ["-c", "%u %a", "--", entry])).Trim().Split(' ');
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
        ValidatePaths(registration.Binary, Root + "/config.json", registration.ConfigurationHash);
        if (registration.BinaryHash.Length != 64 || !registration.BinaryHash.All(Uri.IsHexDigit)
            || await File.ReadAllTextAsync(UnitPath, ct) != UnitText(registration)) Fail("external_installation");
        return registration;
    }

    private static async Task<Dictionary<string, string>> StateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var output = await SingBoxOperations.RunAsync("/usr/bin/systemctl", ["show", Unit,
            "--property=LoadState,ActiveState,SubState,MainPID,UnitFileState,FragmentPath,DropInPaths,NeedDaemonReload"]);
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
        if (!arguments.SequenceEqual(new[] { registration.Binary, "run", "-c", Credential, "" })) Fail("state_unknown");
        await RequireProtectedAsync(registration.Binary);
        if (!string.Equals(await DigestAsync(proc + "/exe", ct), registration.BinaryHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(await DigestAsync(proc + "/root" + Credential, ct), registration.ConfigurationHash, StringComparison.OrdinalIgnoreCase)) Fail("configuration_drift");
        using var after = Process.GetProcessById(processId);
        if (after.StartTime != started) Fail("state_unknown");
        return new(processId, started, registration.ConfigurationHash);
    }

    public async Task<SingBoxServiceObservation> ObserveAsync(CancellationToken ct)
    {
        var registration = await ReadRegistrationAsync(ct);
        if (registration is null)
        {
            // A vendor unit or transient service is still an external installation.
            var load = (await SingBoxOperations.RunAsync("/usr/bin/systemctl", ["show", Unit, "--property=LoadState", "--value"])).Trim();
            if (load != "not-found") Fail("external_installation");
            return new(false, true, false, false, null, null);
        }
        var state = await StateAsync(ct);
        bool? boot = state.GetValueOrDefault("UnitFileState") switch { "enabled" => true, "disabled" => false, _ => null };
        var process = await VerifyProcessAsync(registration, state, ct);
        return new(true, true, process is not null, boot, registration.Binary, process?.Identity);
    }

    public async Task ConfigureAsync(string verifiedBinary, string protectedConfiguration, string configurationIdentity, bool bootEnabled, CancellationToken ct)
    {
        ValidatePaths(verifiedBinary, protectedConfiguration, configurationIdentity);
        var previous = await ReadRegistrationAsync(ct);
        if (previous is null) await ObserveAsync(ct);
        else if ((await StateAsync(ct)).GetValueOrDefault("MainPID") != "0") Fail("state_unknown");
        await RequireProtectedAsync(verifiedBinary); await RequireProtectedAsync(protectedConfiguration);
        if (!string.Equals(await DigestAsync(protectedConfiguration, ct), configurationIdentity, StringComparison.OrdinalIgnoreCase)) Fail("configuration_drift");
        var registration = new Registration(verifiedBinary, await DigestAsync(verifiedBinary, ct), configurationIdentity);
        await WriteAsync(RegistrationPath, JsonSerializer.Serialize(registration), UnixFileMode.UserRead | UnixFileMode.UserWrite, ct);
        await WriteAsync(UnitPath, UnitText(registration), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, ct);
        await CommandAsync("daemon-reload", ct, false);
        await CommandAsync(bootEnabled ? "enable" : "disable", ct);
    }

    private static async Task WriteAsync(string path, string text, UnixFileMode mode, CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(path); RuntimeSourceStore.RequireNoLinks(path + ".new");
        await using (var output = new FileStream(path + ".new", new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = mode }))
        { await output.WriteAsync(Encoding.UTF8.GetBytes(text), ct); output.Flush(true); }
        File.Move(path + ".new", path, true);
    }

    public async Task StopAsync(CancellationToken ct)
    { if (await ReadRegistrationAsync(ct) is not null) { await StateAsync(ct); await CommandAsync("stop", ct); } else await ObserveAsync(ct); }
    public async Task StartAsync(CancellationToken ct)
    {
        var registration = await ReadRegistrationAsync(ct) ?? throw new RuntimePolicyException("runtime.state_unknown");
        await StateAsync(ct); await RequireProtectedAsync(registration.Binary); await RequireProtectedAsync(Root + "/config.json");
        if (!string.Equals(await DigestAsync(registration.Binary, ct), registration.BinaryHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(await DigestAsync(Root + "/config.json", ct), registration.ConfigurationHash, StringComparison.OrdinalIgnoreCase)) Fail("configuration_drift");
        await CommandAsync("start", ct); await ObserveAsync(ct);
    }
    public async Task RemoveOwnedRegistrationAsync(CancellationToken ct)
    {
        if (await ReadRegistrationAsync(ct) is null) { await ObserveAsync(ct); return; }
        await StateAsync(ct); await CommandAsync("disable", ct); await CommandAsync("stop", ct);
        File.Delete(UnitPath); File.Delete(RegistrationPath); await CommandAsync("daemon-reload", ct, false);
    }
    public async Task<bool> ListenersHealthyAsync(ProxyServerConfiguration published, CancellationToken ct)
    {
        var registration = await ReadRegistrationAsync(ct);
        if (registration is null) return false;
        var before = await VerifyProcessAsync(registration, await StateAsync(ct), ct);
        if (before is null) return false;
        var listeners = LinuxOwnedListeners.Read(before.Pid, before.StartedAt);
        var healthy = published.Inbounds.Where(i => i.Enabled).All(i => listeners.Any(e => e.Udp == ProxyServerPresets.IsUdp(i.Protocol)
            && e.Port == i.ListenPort && e.Address.Equals(IPAddress.Parse(i.ListenAddress))));
        var after = await VerifyProcessAsync(registration, await StateAsync(ct), ct);
        return healthy && before == after;
    }
    private static async Task CommandAsync(string verb, CancellationToken ct, bool unit = true)
    { ct.ThrowIfCancellationRequested(); await SingBoxOperations.RunAsync("/usr/bin/systemctl", unit ? [verb, Unit] : [verb]); }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string code) => throw new RuntimePolicyException("runtime." + code);
}
