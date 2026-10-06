using System.Diagnostics;
using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Only active Ethernet/Wi-Fi profiles owned by NetworkManager are writable. NetworkManager owns timed recovery.</summary>
internal static class LinuxHostNetworkOperations
{
    private const string Nmcli = "/usr/bin/nmcli";
    private const string Busctl = "/usr/bin/busctl";
    private const string BusName = "org.freedesktop.NetworkManager";
    private const string BusPath = "/org/freedesktop/NetworkManager";

    public static async Task<HostNetworkSnapshot> ReadAsync(HostNetworkSnapshot snapshot)
    {
        if (!File.Exists(Nmcli) || !File.Exists(Busctl)) return snapshot;
        var adapters = new List<HostNetworkAdapter>();
        foreach (var adapter in snapshot.Adapters)
        {
            if (adapter.Kind == "other" || !adapter.Connected) { adapters.Add(adapter); continue; }
            try
            {
                var uuid = await ProfileAsync(adapter.Name);
                var method = await PropertyAsync(uuid, "ipv4.method");
                var ignoreAutoDns = await PropertyAsync(uuid, "ipv4.ignore-auto-dns");
                var configuredDns = await PropertyAsync(uuid, "ipv4.dns");
                var configuredAddresses = await PropertyAsync(uuid, "ipv4.addresses");
                var configuredGateway = await PropertyAsync(uuid, "ipv4.gateway");
                var dns = (await RunAsync(Nmcli, ["--escape", "no", "-g", "IP4.DNS,IP6.DNS", "device", "show", adapter.Name]))
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(d => System.Net.IPAddress.TryParse(d, out _)).ToArray();
                var supported = method is "auto" or "manual" && !configuredAddresses.Contains(',');
                var dhcp = method == "auto";
                adapters.Add(adapter with { Dhcp = dhcp, AutomaticDns = ignoreAutoDns == "no" && configuredDns.Length == 0,
                    DnsServers = dns, CanConfigure = supported,
                    UnavailableReason = supported ? null : "settings.network.complex_configuration",
                    Revision = SettingsRevisions.Hash(adapter.Revision + uuid + method + ignoreAutoDns + configuredDns + configuredAddresses + configuredGateway) });
            }
            catch (IOException) { adapters.Add(adapter); }
        }
        return snapshot with { Adapters = adapters };
    }

    public static async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request)
    {
        if (!File.Exists(Nmcli) || !File.Exists(Busctl)) return Fail(PrivilegedProblemCode.UnsupportedOperation);
        try
        {
            if (request.Operation == PrivilegedOperationKind.HostNetworkConfirm)
            {
                if (!ValidCheckpoint(request.NetworkCheckpoint)) return Fail(PrivilegedProblemCode.InvalidRequest);
                await CallAsync("CheckpointDestroy", "o", request.NetworkCheckpoint!);
                return new(true);
            }
            if (request.NetworkCheckpoint is not null || request.NetworkChange is not { } change || !HostNetworkValidation.IsValid(change))
                return Fail(PrivilegedProblemCode.InvalidRequest);
            var snapshot = await ReadAsync(HostNetworkOperations.Read());
            var adapter = snapshot.Adapters.SingleOrDefault(a => a.Id == change.AdapterId);
            if (adapter?.CanConfigure != true) return Fail(PrivilegedProblemCode.UnsupportedOperation);
            if (request.ExpectedRevision != adapter.Revision) return Fail(PrivilegedProblemCode.Conflict);
            var uuid = await ProfileAsync(adapter.Name);
            var devicePath = (await RunAsync(Nmcli, ["-g", "GENERAL.DBUS-PATH", "device", "show", adapter.Name])).Trim();
            if (!Regex.IsMatch(devicePath, "^/org/freedesktop/NetworkManager/Devices/[0-9]+$")) return Fail(PrivilegedProblemCode.InvalidRequest);
            // No overlapping checkpoints. NM saves the active profile and restores it itself after the timeout.
            var checkpointOutput = await CallAsync("CheckpointCreate", "aouu", "1", devicePath, "120", "0");
            var checkpoint = Regex.Match(checkpointOutput, "\"(/org/freedesktop/NetworkManager/Checkpoint/[0-9]+)\"").Groups[1].Value;
            if (!ValidCheckpoint(checkpoint)) return Fail(PrivilegedProblemCode.InternalError);
            try
            {
                await RunAsync(Nmcli, ["connection", "modify", "uuid", uuid,
                    "ipv4.method", change.Dhcp ? "auto" : "manual",
                    "ipv4.addresses", change.Dhcp ? "" : $"{change.Address}/{change.PrefixLength}",
                    "ipv4.gateway", change.Gateway ?? "",
                    "ipv4.ignore-auto-dns", change.AutomaticDns ? "no" : "yes",
                    "ipv4.dns", string.Join(",", change.DnsServers)]);
                await RunAsync(Nmcli, ["--wait", "15", "device", "reapply", adapter.Name]);
                return new(true, NetworkCheckpoint: checkpoint);
            }
            catch (IOException)
            {
                try { await CallAsync("CheckpointRollback", "o", checkpoint); } catch (IOException) { }
                return Fail(PrivilegedProblemCode.InternalError);
            }
        }
        catch (IOException) { return Fail(PrivilegedProblemCode.InternalError); }
    }

    private static bool ValidCheckpoint(string? value) => value is not null
        && Regex.IsMatch(value, "^/org/freedesktop/NetworkManager/Checkpoint/[0-9]+$");
    private static async Task<string> ProfileAsync(string name)
    {
        var uuid = (await RunAsync(Nmcli, ["-g", "GENERAL.CON-UUID", "device", "show", name])).Trim();
        return Guid.TryParse(uuid, out _) ? uuid : throw new IOException("Unmanaged adapter");
    }
    private static async Task<string> PropertyAsync(string uuid, string property)
        => (await RunAsync(Nmcli, ["--escape", "no", "-g", property, "connection", "show", "uuid", uuid])).Trim();
    private static Task<string> CallAsync(string method, string signature, params string[] values)
        => RunAsync(Busctl, ["--system", "call", BusName, BusPath, BusName, method, signature, .. values]);

    private static async Task<string> RunAsync(string executable, string[] arguments)
    {
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true } };
        TrustedProcessEnvironment.Apply(process.StartInfo);
        process.StartInfo.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception) { throw new IOException("Network provider unavailable"); }
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw new IOException("Network provider timed out"); }
        await error;
        if (process.ExitCode != 0) throw new IOException("Network provider rejected operation");
        return await output;
    }
    private static PrivilegedOperationResult Fail(PrivilegedProblemCode code) => new(false, 1, ProblemCode: code);
}
