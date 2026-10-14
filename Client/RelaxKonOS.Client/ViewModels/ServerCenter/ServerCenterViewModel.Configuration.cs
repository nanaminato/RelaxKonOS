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
    public string ComponentsText => T("server_center.components", "Components");
    public string SettingsText => T("server_center.installation_settings", "Installation settings");
    public string ApplyConfigurationText => T("server_center.apply_configuration", "Apply and verify");
    public string GuardianChoiceText => T("server_center.guardian_choice", "Install process guardian");
    public string ConfigurationLanText => T("server_center.configuration_lan", "Allow LAN access (all network interfaces)");
    public string ConfigurationPortText => T("server_center.configuration_port", "Listening port");
    public string ConfigurationNote => T("server_center.configuration_note", "Applying changes restarts services. Removing Guardian stops its workloads and retains definitions and logs. TLS identity is preserved. Review the address after applying.");
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
            ConfigurationGuardian && !CanConfigureGuardian)
        {
            ErrorMessage = T("server_center.configuration_unknown", "Refresh the installation first; complete configuration facts are required.");
            return;
        }
        await PerformInstalledOperationAsync(ServerDeploymentKind.Configure, ServerDataRetention.Retain, cancellationToken);
    }
}
