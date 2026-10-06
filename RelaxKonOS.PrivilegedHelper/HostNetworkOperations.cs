using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Closed IPv4 operations. A SYSTEM scheduled task restores connectivity independently of Server/Client lifetimes.</summary>
internal static class HostNetworkOperations
{
    private static readonly SemaphoreSlim Gate = new(1);
    private const string TaskPrefix = "RelaxKonOS-Network-";
    public static async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request)
    {
        var clean = new PrivilegedOperationRequest(request.Operation, ExpectedRevision: request.ExpectedRevision,
            OperationId: request.OperationId, Correlation: request.Correlation, NetworkChange: request.NetworkChange, NetworkCheckpoint: request.NetworkCheckpoint);
        if (clean != request || request.Operation == PrivilegedOperationKind.HostNetworkRead
            && (request.NetworkChange is not null || request.ExpectedRevision is not null || request.NetworkCheckpoint is not null)
            || request.Operation == PrivilegedOperationKind.HostNetworkConfirm
            && (request.NetworkChange is not null || request.ExpectedRevision is not null)) return Fail(PrivilegedProblemCode.InvalidRequest);
        if (request.Operation == PrivilegedOperationKind.HostNetworkRead)
            return new(true, HostNetwork: OperatingSystem.IsLinux() ? await LinuxHostNetworkOperations.ReadAsync(Read()) : Read());
        if (OperatingSystem.IsLinux()) return await LinuxHostNetworkOperations.ExecuteAsync(request);
        if (!OperatingSystem.IsWindows()) return Fail(PrivilegedProblemCode.UnsupportedOperation);
        if (request.NetworkCheckpoint is not null) return Fail(PrivilegedProblemCode.InvalidRequest);
        await Gate.WaitAsync();
        try
        {
            var taskName = TaskPrefix + request.OperationId!.Value.ToString("N");
            if (request.Operation == PrivilegedOperationKind.HostNetworkConfirm)
            {
                // A missing/running recovery task cannot be cancelled as a successful confirmation.
                await PowerShellAsync($"$t=Get-ScheduledTask -TaskName '{taskName}' -ErrorAction Stop; if($t.State -eq 'Running'){{throw 'recovery started'}}; Unregister-ScheduledTask -TaskName '{taskName}' -Confirm:$false -ErrorAction Stop");
                return new(true);
            }
            if (request.NetworkChange is not { } change || !HostNetworkValidation.IsValid(change)) return Fail(PrivilegedProblemCode.InvalidRequest);
            if (!Guid.TryParse(change.AdapterId, out var adapterId)) return Fail(PrivilegedProblemCode.InvalidRequest);
            var adapter = Read().Adapters.SingleOrDefault(a => Guid.Parse(a.Id) == adapterId);
            if (adapter is null || !adapter.CanConfigure) return Fail(PrivilegedProblemCode.UnsupportedOperation);
            if (adapter.Revision != request.ExpectedRevision) return Fail(PrivilegedProblemCode.Conflict);
            await PowerShellAsync($"if(@(Get-ScheduledTask -TaskName '{TaskPrefix}*' -ErrorAction SilentlyContinue).Count -gt 0){{throw 'network operation pending'}}");
            var ipv4 = adapter.Addresses.FirstOrDefault(a => a.Address.Contains('.'));
            var restore = new HostNetworkChange(adapter.Id, adapter.Dhcp, adapter.Dhcp ? null : ipv4?.Address,
                ipv4?.PrefixLength ?? 24, adapter.Dhcp ? null : adapter.Gateways.FirstOrDefault(g => g.Contains('.')),
                adapter.AutomaticDns, adapter.AutomaticDns ? [] : adapter.DnsServers.Where(d => d.Contains('.')).ToArray());
            if (!HostNetworkValidation.IsValid(restore)) return Fail(PrivilegedProblemCode.UnsupportedOperation);
            var rollback = BuildScript(adapter.Index, restore)
                + $"\nUnregister-ScheduledTask -TaskName '{taskName}' -Confirm:$false -ErrorAction SilentlyContinue";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(rollback));
            // Register and verify the watchdog before changing any address. Task Scheduler persists it across restarts.
            await PowerShellAsync($"$a=New-ScheduledTaskAction -Execute ([IO.Path]::Combine($env:SystemRoot,'System32','WindowsPowerShell','v1.0','powershell.exe')) -Argument '-NoProfile -NonInteractive -EncodedCommand {encoded}';"
                + "$tr=New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds(120);"
                + "$s=New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 2);"
                + $"Register-ScheduledTask -TaskName '{taskName}' -Action $a -Trigger $tr -Settings $s -User 'SYSTEM' -RunLevel Highest -ErrorAction Stop | Out-Null;");
            try { await PowerShellAsync(BuildScript(adapter.Index, change)); }
            catch
            {
                // Keep the scheduled recovery if immediate compensation fails.
                try { await PowerShellAsync(rollback); } catch { }
                throw;
            }
            return new(true);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return Fail(PrivilegedProblemCode.InternalError); }
        finally { Gate.Release(); }
    }

    internal static HostNetworkSnapshot Read()
    {
        var adapters = new List<HostNetworkAdapter>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            var properties = nic.GetIPProperties();
            var addresses = properties.UnicastAddresses.Select(a => new HostNetworkAddress(a.Address.ToString(), a.PrefixLength))
                .OrderBy(a => a.Address, StringComparer.Ordinal).ToArray();
            var gateways = properties.GatewayAddresses.Select(a => a.Address.ToString()).Order(StringComparer.Ordinal).ToArray();
            var dns = properties.DnsAddresses.Select(a => a.ToString()).ToArray();
            var ipv4 = properties.GetIPv4Properties();
            var dhcp = OperatingSystem.IsWindows() && ipv4?.IsDhcpEnabled == true;
            var automaticDns = true;
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + nic.Id);
                automaticDns = string.IsNullOrWhiteSpace(key?.GetValue("NameServer") as string);
            }
            var kind = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "wifi"
                : nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? "ethernet" : "other";
            var supported = OperatingSystem.IsWindows() && Guid.TryParse(nic.Id, out _) && ipv4 is not null
                && kind != "other" && addresses.Count(a => a.Address.Contains('.')) <= 1
                && gateways.Count(a => a.Contains('.')) <= 1
                && (dhcp || addresses.Any(a => a.Address.Contains('.')))
                && (automaticDns || dns.Any(a => a.Contains('.')));
            var revision = SettingsRevisions.Hash(JsonSerializer.Serialize(new { nic.Id, Index = ipv4?.Index, dhcp, automaticDns, addresses, gateways, dns }));
            adapters.Add(new(nic.Id, ipv4?.Index ?? 0, nic.Name, nic.Description, kind,
                nic.OperationalStatus == OperationalStatus.Up, nic.Speed, nic.GetPhysicalAddress().ToString(),
                dhcp, automaticDns, addresses, gateways, dns, revision, supported,
                supported ? null : OperatingSystem.IsWindows() ? "settings.network.complex_configuration" : "settings.network.read_only_platform"));
        }
        return new(adapters.OrderByDescending(a => a.Connected).ThenBy(a => a.Name).ToArray(),
            OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other");
    }

    internal static string BuildScript(int index, HostNetworkChange change)
    {
        if (index <= 0 || !HostNetworkValidation.IsValid(change)) throw new InvalidOperationException("Invalid network change");
        var id = Guid.Parse(change.AdapterId).ToString("D");
        var script = "$ErrorActionPreference='Stop';"
            + $"$n=@(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object {{$_.InterfaceGuid -eq [Guid]'{id}'}}); if($n.Count -ne 1){{throw 'adapter changed'}}; $idx=$n[0].ifIndex;"
            + "$netsh=[IO.Path]::Combine($env:SystemRoot,'System32','netsh.exe');";
        void Command(string arguments) => script += $" & $netsh {arguments.Replace($"name={index}", "name=$idx")}; if($LASTEXITCODE -ne 0){{throw 'network configuration rejected'}};";
        if (change.Dhcp) Command($"interface ipv4 set address name={index} source=dhcp");
        else
        {
            var mask = uint.MaxValue << (32 - change.PrefixLength);
            var maskText = $"{mask >> 24}.{(mask >> 16) & 255}.{(mask >> 8) & 255}.{mask & 255}";
            Command($"interface ipv4 set address name={index} source=static address={change.Address} mask={maskText} gateway={(string.IsNullOrEmpty(change.Gateway) ? "none" : change.Gateway)}");
        }
        if (change.AutomaticDns) Command($"interface ipv4 set dnsservers name={index} source=dhcp");
        else
        {
            Command($"interface ipv4 set dnsservers name={index} source=static address={change.DnsServers[0]} validate=no");
            for (var i = 1; i < change.DnsServers.Count; i++)
                Command($"interface ipv4 add dnsservers name={index} address={change.DnsServers[i]} index={i + 1} validate=no");
        }
        return script;
    }

    private static async Task PowerShellAsync(string script)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true } };
        TrustedProcessEnvironment.Apply(process.StartInfo);
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw new IOException("Network operation timed out"); }
        await output; await error;
        if (process.ExitCode != 0) throw new IOException("Network operation failed");
    }

    private static PrivilegedOperationResult Fail(PrivilegedProblemCode code) => new(false, 1, ProblemCode: code);
}
