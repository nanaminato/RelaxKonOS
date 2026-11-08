using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;


namespace RelaxKonOS.PrivilegedHelper;

/// <summary>One fixed systemd scope. Registration metadata alone never proves a running configuration.</summary>
[SupportedOSPlatform("linux")]
internal sealed partial class LinuxMihomoArchiveHost : IMihomoArchiveHost
{
    internal const string Root = "/var/lib/relaxkonos/managed-mihomo";
    internal const string Unit = "relaxkonos-mihomo.service";
    private const string UnitPath = "/etc/systemd/system/" + Unit;
    private const string Credential = "/run/credentials/" + Unit + "/active.yaml";
    private const string RegistrationPath = Root + "/archive-service.json";
    internal const string TunLauncher = "/usr/local/lib/relaxkonos/privileged-helper/RelaxKonOS.PrivilegedHelper";
    internal const string TunLaunchArgument = "--linux-mihomo-tun-launch";
    private const int TunFd = 198;
    private sealed record Registration(string Binary, string Data, string BinaryHash, string ConfigurationHash, string Version,
        Guid? TunOperation = null);
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

    private static string UnitText(Registration registration)
    {
        var tun = registration.TunOperation is not null;
        var command = tun ? $"{TunLauncher} {TunLaunchArgument}" : $"{registration.Binary} -d {registration.Data} -f %d/active.yaml";
        var restart = tun ? "no" : "on-failure";
        return $"# RelaxKonOS managed archive Mihomo\n[Unit]\nDescription=RelaxKonOS Mihomo\nAfter=network-online.target\n[Service]\nType=simple\nUser=root\nLoadCredential=active.yaml:{Root}/active.yaml\nExecStart={command}\nRestart={restart}\nNoNewPrivileges=yes\nPrivateTmp=yes\nPrivateDevices=yes\nProtectSystem=strict\nProtectHome=yes\nInaccessiblePaths=-/etc/relaxkonos -/opt/relaxkonos\nReadWritePaths={registration.Data}\nAmbientCapabilities=CAP_NET_BIND_SERVICE\nCapabilityBoundingSet=CAP_NET_BIND_SERVICE\nRestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_NETLINK\n[Install]\nWantedBy=multi-user.target\n";
    }

    private static void RequireProtected(string path)
    {
        try
        {
            for (string? entry = path; entry is not null; entry = Path.GetDirectoryName(entry))
                RuntimeSourceProtection.RequireRootOwned(entry);
        }
        catch (UnauthorizedAccessException) { Fail("service_scope_invalid"); }
    }

    private static async Task<Registration?> ReadRegistrationAsync(CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(UnitPath);
        RuntimeSourceStore.RequireNoLinks(RegistrationPath);
        if (!File.Exists(UnitPath) && !File.Exists(RegistrationPath)) return null;
        if (!File.Exists(UnitPath) || !File.Exists(RegistrationPath)) Fail("external_installation");
        RequireProtected(UnitPath);
        RequireProtected(RegistrationPath);
        if (new FileInfo(RegistrationPath).Length > 8192 || new FileInfo(UnitPath).Length > 8192) Fail("service_scope_invalid");
        var registration = JsonSerializer.Deserialize<Registration>(await File.ReadAllTextAsync(RegistrationPath, ct));
        if (registration is null) Fail("state_unknown");
        ValidatePaths(registration.Binary, registration.Data, Root + "/active.yaml", registration.ConfigurationHash);
        if (registration.BinaryHash.Length != 64 || !registration.BinaryHash.All(Uri.IsHexDigit)
            || registration.TunOperation == Guid.Empty
            || MihomoRuntimeSupport.CapabilityFor(registration.Version) is null
            || await File.ReadAllTextAsync(UnitPath, ct) != UnitText(registration)) Fail("external_installation");
        return registration;
    }

    private static async Task<Dictionary<string, string>> StateAsync(CancellationToken ct, bool allowReload = false)
    {
        ct.ThrowIfCancellationRequested();
        var output = await RunAsync("/usr/bin/systemctl", ["show", Unit,
            "--property=LoadState,ActiveState,SubState,MainPID,UnitFileState,FragmentPath,DropInPaths,NeedDaemonReload,Result,InvocationID"]);
        var state = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts.Length == 2 ? parts[1] : "");
        if (state.GetValueOrDefault("LoadState") != "loaded" || state.GetValueOrDefault("FragmentPath") != UnitPath
            || state.GetValueOrDefault("DropInPaths") != ""
            || state.GetValueOrDefault("NeedDaemonReload") != "no" && (!allowReload || state.GetValueOrDefault("NeedDaemonReload") != "yes"))
            Fail("state_unknown");
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
        RequireProtected(registration.Binary);
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
        RequireProtected(registration.Binary);
        RequireProtected(Root + "/active.yaml");
        RequireProtected(registration.Data);
        bool? boot = state.GetValueOrDefault("UnitFileState") switch { "enabled" => true, "disabled" => false, _ => null };
        var process = await VerifyProcessAsync(registration, state, ct);
        return new(true, true, process is not null, boot, registration.Binary, process?.Identity, registration.Data, registration.TunOperation);
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
        string configurationIdentity, bool bootEnabled, CancellationToken ct) =>
        await ConfigureCoreAsync(verifiedBinary, protectedDataDirectory, protectedConfiguration, configurationIdentity,
            bootEnabled, null, ct);

    internal async Task ConfigureTunAsync(string binary, string data, string configurationIdentity, Guid operation, CancellationToken ct)
    {
        ValidatePaths(binary, data, Root + "/active.yaml", configurationIdentity);
        var checkpoint = TunCheckpoint(binary, operation, configurationIdentity);
        if (checkpoint.Read() is not { Phase: MihomoTunPhase.NetworkDispatched } armed
            || armed.BrokerPid != Environment.ProcessId) Fail("network_checkpoint_conflict");
        RequireProtected(TunLauncher);
        RequireProtected(checkpoint.SocketPath);
        await ConfigureCoreAsync(binary, data, Root + "/active.yaml", configurationIdentity, false, operation, ct);
    }

    internal async Task DispatchTunStartAsync(Guid operation, CancellationToken ct)
    {
        var registration = await ReadRegistrationAsync(ct);
        if (registration is null || registration.TunOperation != operation) Fail("network_checkpoint_conflict");
        var checkpoint = TunCheckpoint(registration.Binary, operation, registration.ConfigurationHash);
        if (checkpoint.Read() is not { Phase: MihomoTunPhase.NetworkDispatched } armed
            || armed.BrokerPid != Environment.ProcessId)
            throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
        var state = await StateAsync(ct);
        if (state.GetValueOrDefault("ActiveState") != "inactive" || state.GetValueOrDefault("UnitFileState") != "disabled")
            Fail("state_unknown");
        ct.ThrowIfCancellationRequested();
        await checkpoint.AppendAsync(armed with { Phase = MihomoTunPhase.LaunchDispatched });
        await RunAsync("/usr/bin/systemctl", ["start", "--no-block", Unit]);
    }

    private async Task ConfigureCoreAsync(string verifiedBinary, string protectedDataDirectory, string protectedConfiguration,
        string configurationIdentity, bool bootEnabled, Guid? tunOperation, CancellationToken ct)
    {
        ValidatePaths(verifiedBinary, protectedDataDirectory, protectedConfiguration, configurationIdentity);
        var previous = await ReadRegistrationAsync(ct);
        if (previous is null) await ObserveAsync(ct);
        else
        {
            if (previous.TunOperation is { } original
                && TunCheckpoint(previous.Binary, original, previous.ConfigurationHash).Read()?.Phase != MihomoTunPhase.Restored)
                Fail("network_transaction_required");
            if ((await StateAsync(ct)).GetValueOrDefault("MainPID") != "0") Fail("state_unknown");
        }
        RequireProtected(verifiedBinary); RequireProtected(protectedConfiguration);
        RequireProtected(protectedDataDirectory);
        if (!string.Equals(await DigestAsync(protectedConfiguration, ct), configurationIdentity, StringComparison.OrdinalIgnoreCase)) Fail("configuration_drift");
        var report = await MihomoOperations.VersionAsync(verifiedBinary);
        var tag = report.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(2) ?? "";
        var version = MihomoNativeValidation.Parse(report, tag, "linux", MihomoNativeValidation.ArchitectureName).Version;
        if (MihomoRuntimeSupport.CapabilityFor(version) is null) Fail("configuration_unsupported");
        var registration = new Registration(verifiedBinary, protectedDataDirectory, await DigestAsync(verifiedBinary, ct),
            configurationIdentity, version, tunOperation);
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
        var registration = await ReadRegistrationAsync(ct) ?? throw new RuntimePolicyException("runtime.state_unknown");
        if (enabled && registration.TunOperation is not null) Fail("network_transaction_required");
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
        if (await ReadRegistrationAsync(ct) is { } registration)
        {
            if (registration.TunOperation is not null) Fail("network_transaction_required");
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
    public Task StartAsync(CancellationToken ct) => StartCoreAsync(null, ct);

    internal Task StartRestoredTunAsync(Guid operation, CancellationToken ct) => StartCoreAsync(operation, ct);

    private async Task StartCoreAsync(Guid? restoredOperation, CancellationToken ct)
    {
        var registration = await ReadRegistrationAsync(ct) ?? throw new RuntimePolicyException("runtime.state_unknown");
        if (registration.TunOperation is not null) Fail("network_transaction_required");
        await StateAsync(ct); RequireProtected(registration.Binary); RequireProtected(Root + "/active.yaml");
        RequireProtected(registration.Data);
        if (!string.Equals(await DigestAsync(registration.Binary, ct), registration.BinaryHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(await DigestAsync(Root + "/active.yaml", ct), registration.ConfigurationHash, StringComparison.OrdinalIgnoreCase)) Fail("configuration_drift");
        // This is an explicit, verified management start, including compensation. Clear a prior failed
        // owned unit, and retry exactly once only for systemd's measured start-limit-hit result. Inactive
        // disabled units can be garbage-collected, so reset-failed must not be sent unconditionally.
        var initial = await StateAsync(ct);
        if (restoredOperation is not null && (initial.GetValueOrDefault("MainPID") != "0"
            || initial.GetValueOrDefault("UnitFileState") != "disabled"
            || initial.GetValueOrDefault("ActiveState") is not ("inactive" or "failed"))) Fail("state_unknown");
        if (initial.GetValueOrDefault("ActiveState") == "failed") await CommandAsync("reset-failed", ct);
        if (restoredOperation is { } operation) await ArmRestoredTunStartAsync(operation, ct);
        try { await CommandAsync("start", ct); }
        catch (InvalidOperationException) when (restoredOperation is null)
        {
            var failed = await StateAsync(ct);
            if (failed.GetValueOrDefault("ActiveState") != "failed" || failed.GetValueOrDefault("Result") != "start-limit-hit") throw;
            await CommandAsync("reset-failed", ct);
            await CommandAsync("start", ct);
        }
        // A simple unit may report running before ExecStart has switched to the registered binary.
        // Wait for that transition without treating a launcher as the owned Mihomo process.
        await WaitAsync(state => state.GetValueOrDefault("ActiveState") is "inactive" or "failed"
            || state.GetValueOrDefault("ActiveState") == "active" && state.GetValueOrDefault("SubState") == "running"
            && int.TryParse(state.GetValueOrDefault("MainPID"), out var pid) && pid > 1
            && new FileInfo($"/proc/{pid}/exe").LinkTarget == registration.Binary, ct);
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
        var registration = await ReadRegistrationAsync(ct);
        if (registration is null) { await ObserveAsync(ct); return; }
        if (registration.TunOperation is not null) Fail("network_transaction_required");
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

    private static LinuxMihomoTunCheckpoint TunCheckpoint(string binary, Guid operation, string identity) =>
        new(Root, operation, Guid.ParseExact(Path.GetFileName(Path.GetDirectoryName(binary))!, "N"), identity);

    internal static async Task LaunchTunAsync()
    {
        if (!RuntimeSourceProtection.IsAdministrator() || Environment.ProcessPath != TunLauncher)
            Fail("service_scope_invalid");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = deadline.Token;
        var registration = await ReadRegistrationAsync(ct);
        if (registration?.TunOperation is not { } operation)
            throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
        var state = await StateAsync(ct);
        var invocation = state.GetValueOrDefault("InvocationID");
        if (state.GetValueOrDefault("MainPID") != Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            || state.GetValueOrDefault("UnitFileState") != "disabled"
            || !Guid.TryParseExact(invocation, "N", out var invocationId) || invocationId == Guid.Empty
            || Environment.GetEnvironmentVariable("INVOCATION_ID") != invocation)
            throw new RuntimePolicyException("runtime.service_scope_invalid");
        var stat = await File.ReadAllTextAsync("/proc/self/stat", ct);
        var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || !ulong.TryParse(fields[19], CultureInfo.InvariantCulture, out var startTicks))
            throw new RuntimePolicyException("runtime.state_unknown");

        RequireProtected(registration.Binary);
        RequireProtected(registration.Data);
        if (!string.Equals(await DigestAsync(registration.Binary, ct), registration.BinaryHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(await DigestAsync(Credential, ct), registration.ConfigurationHash, StringComparison.OrdinalIgnoreCase))
            Fail("configuration_drift");
        var checkpoint = TunCheckpoint(registration.Binary, operation, registration.ConfigurationHash);
        if (checkpoint.Read() is not { Phase: MihomoTunPhase.LaunchDispatched } armed
            || armed.Intent.BootId != LinuxMihomoTunCheckpoint.BootId()
            || armed.Intent.NetworkNamespace != LinuxMihomoTunCheckpoint.NetworkNamespace())
            throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
        RequireProtected(checkpoint.SocketPath);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(checkpoint.SocketPath), ct);
        using var descriptor = LinuxMihomoTunDescriptor.ReceiveFrom(socket, operation, armed.BrokerPid, 0,
            armed.Intent.Device, armed.InterfaceIndex);
        socket.Dispose();
        if (checkpoint.Read() != (armed with { Phase = MihomoTunPhase.DescriptorDispatched,
                Process = new(Environment.ProcessId, invocationId, startTicks) }))
            Fail("network_checkpoint_conflict");
        ExecTunMihomo(descriptor, registration);
    }

    private static void ExecTunMihomo(SafeFileHandle descriptor, Registration registration)
    {
        var arguments = new[] { registration.Binary, "-d", registration.Data, "-f", Credential };
        var pointers = new nint[arguments.Length];
        var vector = Marshal.AllocHGlobal((pointers.Length + 1) * nint.Size);
        try
        {
            for (var index = 0; index < arguments.Length; index++)
            {
                pointers[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
                Marshal.WriteIntPtr(vector, index * nint.Size, pointers[index]);
            }
            Marshal.WriteIntPtr(vector, pointers.Length * nint.Size, 0);
            const int duplicateDescriptor = 0; // F_DUPFD inherits without replacing an open descriptor.
            var inherited = Fcntl(descriptor, duplicateDescriptor, TunFd);
            using var inheritedHandle = new SafeFileHandle(inherited, ownsHandle: true);
            if (inherited != TunFd) Fail("network_descriptor_unavailable");
            Exec(pointers[0], vector);
            Fail("network_launcher_failed");
        }
        finally
        {
            foreach (var pointer in pointers) Marshal.FreeCoTaskMem(pointer);
            Marshal.FreeHGlobal(vector);
        }
    }

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(SafeFileHandle descriptor, int command, int argument);

    [DllImport("libc", EntryPoint = "execv", SetLastError = true)]
    private static extern int Exec(nint path, nint arguments);

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
