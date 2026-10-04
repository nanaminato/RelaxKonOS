using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.ServerCenter;

public sealed record ServerInstallationDetail(string Label, string Value);

/// <summary>Displays observed installation facts; missing receipt fields stay unknown.</summary>
public static class ServerInstallationDetails
{
    public static IReadOnlyList<ServerInstallationDetail> Build(
        ServerHostTarget host, ServerHostSnapshotDto? snapshot, ServerHostProbeDto? probe,
        Func<string, string, string> translate)
    {
        var rows = new List<ServerInstallationDetail>();
        string T(string key, string fallback) => translate("server_center.installation." + key, fallback);
        string Value(string? value) => string.IsNullOrWhiteSpace(value) ? T("unknown", "Not provided") : value;
        string YesNo(bool value) => value ? T("yes", "Yes") : T("no", "No");
        void Add(string key, string label, string? value) => rows.Add(new(T(key, label), Value(value)));
        var cached = host.LastVerified;
        var mode = snapshot?.Mode ?? cached?.Mode;
        var time = snapshot?.VerifiedAtUtc ?? cached?.VerifiedAtUtc;
        Add("status", "Installation status", (snapshot?.Installed ?? cached?.Installed) == true
            ? T("installed", "Installed") : T("not_installed", "Not installed"));
        Add("verified_at", "SSH verified at", time?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"));
        Add("source", "Information source", snapshot is null
            ? T("cached", "Saved SSH verification; refresh to retrieve full details")
            : T("receipt", "SSH status receipt; reflects the verification time"));
        Add("mode", "Installation mode", mode switch
        {
            ServerInstallMode.LinuxSystem => T("linux_system", "Linux system service (LinuxSystem)"),
            ServerInstallMode.LinuxUser => T("linux_user", "Linux user mode (LinuxUser)"),
            ServerInstallMode.WindowsSystem => T("windows_system", "Windows system service (WindowsSystem)"),
            ServerInstallMode.WindowsUser => T("windows_user", "Windows personal mode (WindowsUser)"),
            _ => null
        });
        Add("mode_description", "How this mode runs", mode switch
        {
            ServerInstallMode.LinuxSystem => T("linux_system_description", "Managed by systemd; maintenance requires root or sudo."),
            ServerInstallMode.LinuxUser => T("linux_user_description", "Runs under the installing user without a system service; no root required."),
            ServerInstallMode.WindowsSystem => T("windows_system_description", "Managed by Windows services; maintenance requires an elevated administrator SSH session."),
            ServerInstallMode.WindowsUser => T("windows_user_description", "Runs as the installation owner after sign-in. UAC authorizes installation and maintenance; an owner-bound Helper executes daily privileged operations."),
            _ => null
        });
        Add("version", "Current version", snapshot?.Version ?? cached?.Version);
        Add("previous_version", "Previous version", snapshot is null ? null :
            string.IsNullOrWhiteSpace(snapshot.PreviousVersion) ? T("no_previous", "No previous version recorded") : snapshot.PreviousVersion);
        Add("id", "Installation ID", snapshot?.InstallationId ?? cached?.InstallationId);
        Add("health", "Health at verification", (snapshot?.Healthy ?? cached?.Healthy) == true
            ? T("healthy", "Health check passed") : T("unhealthy", "Health check did not pass"));
        Add("install_root", "Program directory", snapshot?.InstallRoot);
        Add("data_root", "Managed data directory", snapshot?.DataRoot);
        Add("listen_url", "Server listening address", snapshot?.ListenUrl ?? cached?.ListenUrl);
        Add("services", "Managed service names", snapshot?.ServiceNames is { } services
            ? services.Count == 0 ? T("no_services", "No system services") : string.Join("\n", services) : null);
        Add("ssh_host", "SSH host", host.SshHost);
        Add("ssh_port", "SSH port", host.SshPort.ToString());
        Add("ssh_user", "SSH management user", host.SshUserName);
        if (probe is not null)
        {
            Add("probe_at", "Host preflight at", probe.VerifiedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"));
            Add("os", "Operating system", string.Join(" ", new[] { probe.HostPlatform.ToString(), probe.OsId, probe.OsVersion }.Where(s => !string.IsNullOrWhiteSpace(s))));
            Add("architecture", "Architecture", probe.Architecture);
            Add("runtime", "Runtime identifier", probe.RuntimeIdentifier?.ToString());
            Add("os_supported", "OS in supported matrix", YesNo(probe.OsSupported));
            Add("elevated", "SSH preflight has elevated privileges", YesNo(probe.Elevated));
            Add("sudo", "sudo available", YesNo(probe.SudoAvailable));
            Add("systemd", "systemd available", YesNo(probe.SystemdAvailable));
            Add("disk", "Available space on installation filesystem", probe.DiskAvailableBytes is { } bytes
                ? $"{bytes / 1073741824d:F2} GiB ({bytes:N0} B)" : null);
            Add("dependencies", "Missing dependencies", probe.MissingDependencies.Count == 0
                ? T("none_missing", "None") : string.Join(", ", probe.MissingDependencies));
        }
        return rows;
    }
}
