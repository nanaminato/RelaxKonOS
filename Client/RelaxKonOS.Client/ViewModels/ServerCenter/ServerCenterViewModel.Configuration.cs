using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.ViewModels.ServerCenter;

public partial class ServerCenterViewModel
{
    private ServerHostSnapshotDto? _reviewedConfiguration;
    private string? _reviewedConfigurationHostId;
    [ObservableProperty] private string _configurationPort = "";
    [ObservableProperty] private bool _configurationLan;
    [ObservableProperty] private bool _configurationGuardian;
    [ObservableProperty] private bool _configurationAutoStart;
    public bool CanConfigureAutoStart => _reviewedConfiguration?.Mode != ServerInstallMode.LinuxUser && _reviewedConfiguration?.AutoStart is not null;
    public string AutoStartText => T("server_center.auto_start", "Start automatically (at boot, or sign-in in personal mode)");
    [ObservableProperty] private FileAccessOption? _configurationFileAccess;
    [ObservableProperty] private string _configurationFileRoots = "";
    public IReadOnlyList<FileAccessOption> ConfigurationFileAccesses => [
        new(ServerFileAccessScope.Restricted, T("server_center.wizard.file_access_restricted", "RelaxKonOS data only")),
        new(ServerFileAccessScope.Whitelist, T("server_center.wizard.file_access_whitelist", "Selected directories")),
        new(ServerFileAccessScope.Full, T("server_center.wizard.file_access_full", "All local files"))];
    public bool CanConfigureFileAccess => _reviewedConfiguration?.Mode != ServerInstallMode.LinuxUser && _reviewedConfiguration?.FileAccess is { } scope &&
        (scope != ServerFileAccessScope.Whitelist || _reviewedConfiguration.FileRoots is { Count: > 0 });
    public string FileAccessText => T("server_center.wizard.file_access", "Privileged file access");
    public string FileRootsText => T("server_center.wizard.file_roots", "Allowed directories (one per line)");
    public IReadOnlyList<string>? ConfiguredRoots => ConfigurationFileAccess?.Scope == ServerFileAccessScope.Whitelist
        ? ConfigurationFileRoots.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : null;
    public string RemoveFirewallText => T("server_center.remove_firewall", "Remove RelaxKonOS Server firewall rules");
    public string EnsureFirewallText => T("server_center.ensure_firewall", "Add rule for the current Server listener");
    [RelayCommand(CanExecute = nameof(CanMaintain))]
    private Task EnsureFirewallAsync(CancellationToken cancellationToken = default) => ChangeFirewallAsync(false, cancellationToken);
    [RelayCommand(CanExecute = nameof(CanMaintain))]
    private Task RemoveFirewallAsync(CancellationToken cancellationToken = default) => ChangeFirewallAsync(true, cancellationToken);
    private Task ChangeFirewallAsync(bool remove, CancellationToken cancellationToken)
    {
        if (_reviewedConfigurationHostId is not null && SelectedHost?.HostId != _reviewedConfigurationHostId)
        {
            ErrorMessage = T("server_center.configuration_changed", "The installation changed. Refresh and review the settings again.");
            return Task.CompletedTask;
        }
        if (_reviewedConfiguration is { } reviewed &&
            (ConfigurationPort != new Uri(reviewed.ListenUrl!).Port.ToString() || ConfigurationLan != (new Uri(reviewed.ListenUrl!).Host == "0.0.0.0") ||
             ConfigurationGuardian != reviewed.GuardianEnabled || reviewed.AutoStart != null && ConfigurationAutoStart != reviewed.AutoStart ||
             ConfigurationFileAccess?.Scope != reviewed.FileAccess || ConfigurationFileRoots != string.Join("\n", reviewed.FileRoots ?? [])))
        {
            ErrorMessage = T("server_center.settings_first", "Apply or discard setting changes before firewall actions.");
            return Task.CompletedTask;
        }
        return PerformInstalledOperationAsync(ServerDeploymentKind.Firewall, ServerDataRetention.Retain, cancellationToken, removeFirewallRules: remove);
    }
    public string ComponentsText => T("server_center.components", "Components");
    public string SettingsText => T("server_center.installation_settings", "Installation settings");
    public string ApplyConfigurationText => T("server_center.apply_configuration", "Apply and verify");
    public string GuardianChoiceText => T("server_center.guardian_choice", "Install process guardian");
    public string ConfigurationLanText => T("server_center.configuration_lan", "Allow LAN access (all network interfaces)");
    public string ConfigurationPortText => T("server_center.configuration_port", "Listening port");
    public string ConfigurationNote => T("server_center.configuration_note", "Applying changes restarts services. Removing Guardian stops its workloads and retains definitions and logs. TLS identity is preserved. Review the address after applying.");
    [ObservableProperty] private string _repairPackagePath = "";
    public string RepairPackageText => T("server_center.repair_package", "Server ZIP path (optional; same installed version, restores program files)");
    public string RepairPlanText => T("server_center.repair_plan", "Repair reapplies the current version's services and configuration, retaining the selected components, port, file permissions and TLS identity. Selected Guardian must pass authenticated IPC verification. Certificate regeneration and firewall changes below are explicit additional actions.");
    public string CurrentConfigurationText => _reviewedConfiguration is { } s
        ? $"{s.ListenUrl}\n{GuardianChoiceText}: {s.GuardianEnabled}\n{T("server_center.guardian_running", "Guardian process running")}: {s.GuardianRunning?.ToString() ?? T("server_center.installation.unknown", "Not provided")}" : InstallationInfoNote;
    public bool CanConfigureGuardian => _reviewedConfiguration?.Mode is ServerInstallMode.WindowsSystem or ServerInstallMode.LinuxSystem or ServerInstallMode.LinuxUser;
    public bool HasConfigurationFacts => _reviewedConfiguration is { Installed: true, GuardianEnabled: not null };

    public bool PrepareConfiguration()
    {
        _reviewedConfiguration = null;
        _reviewedConfigurationHostId = null;
        if (SelectedHost is not { } host || !_installationSnapshots.TryGetValue(host.HostId, out var snapshot) ||
            !snapshot.Installed || snapshot.GuardianEnabled is null ||
            !Uri.TryCreate(snapshot.ListenUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || endpoint.Host is not ("127.0.0.1" or "0.0.0.0"))
        {
            ErrorMessage = T("server_center.configuration_unknown", "Refresh the installation first; complete configuration facts are required.");
            return false;
        }
        _reviewedConfiguration = snapshot;
        _reviewedConfigurationHostId = host.HostId;
        ConfigurationPort = endpoint.Port.ToString();
        ConfigurationLan = endpoint.Host == "0.0.0.0";
        ConfigurationGuardian = snapshot.GuardianEnabled.Value;
        ConfigurationAutoStart = snapshot.AutoStart == true;
        ConfigurationFileAccess = ConfigurationFileAccesses.FirstOrDefault(option => option.Scope == snapshot.FileAccess);
        ConfigurationFileRoots = string.Join("\n", snapshot.FileRoots ?? []);
        OnPropertyChanged(nameof(CanConfigureFileAccess));
        OnPropertyChanged(nameof(CanConfigureAutoStart));
        OnPropertyChanged(nameof(CurrentConfigurationText));
        OnPropertyChanged(nameof(CanConfigureGuardian));
        OnPropertyChanged(nameof(HasConfigurationFacts));
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanMaintain))]
    private async Task ConfigureAsync(CancellationToken cancellationToken = default)
    {
        if (!HasConfigurationFacts || SelectedHost?.HostId != _reviewedConfigurationHostId ||
            !int.TryParse(ConfigurationPort, out var port) || port is < 1 or > 65535 ||
            ConfigurationGuardian && !CanConfigureGuardian ||
            CanConfigureFileAccess && ConfigurationFileAccess?.Scope == ServerFileAccessScope.Whitelist &&
                (ConfiguredRoots is not { Count: > 0 } || ConfiguredRoots.Any(root => _reviewedConfiguration?.Mode == ServerInstallMode.LinuxSystem ? !root.StartsWith('/') : !System.IO.Path.IsPathFullyQualified(root))))
        {
            ErrorMessage = T("server_center.configuration_unknown", "Refresh the installation first; complete configuration facts are required.");
            return;
        }
        await PerformInstalledOperationAsync(ServerDeploymentKind.Configure, ServerDataRetention.Retain, cancellationToken);
    }
}
