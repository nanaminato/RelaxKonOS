using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.ViewModels.ServerCenter;

public partial class ServerCenterViewModel
{
    public bool HasManagementStatus => !HasError && !string.IsNullOrWhiteSpace(StatusMessage);
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasManagementStatus));
    public event Action? ConfigurationApplied;

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
    public string BrowseRepairPackageText => T("server_center.browse_repair_package", "Browse server files…");
    public async Task BrowseRepairPackageAsync(Func<Services.ServerCenter.SshDesktopSession, Task<string?>> pick)
    {
        if (IsBusy || SelectedHost is not { } target) return;
        IsBusy = true;
        ErrorMessage = "";
        try
        {
            var credential = await ResolveCredentialAsync(target, default);
            if (credential is not Services.ServerCenter.ServerCenterSshCredential.Password password)
            {
                if (!HasError) ErrorMessage = T("server_center.browser_password_required", "Enter an SSH password to browse this host's files.");
                return;
            }
            await using var connection = await _connections.ConnectAsync(target, credential,
                await _connections.PrepareHostKeyGuardAsync(target, default), default);
            var browser = Services.ServerCenter.SshDesktopSession.ForFileBrowser(connection, password.Secret);
            try
            {
                var path = await pick(browser);
                if (SelectedHost?.HostId == target.HostId && !string.IsNullOrWhiteSpace(path)) RepairPackagePath = path;
            }
            finally { browser.Disconnect(); }
        }
        catch (Services.ServerCenter.ServerCenterHostKeyRejectedException rejected) { await ApplyHostKeyRejectionAsync(rejected, default); }
        catch (OperationCanceledException) { }
        catch (Exception) { ErrorMessage = T("server_center.browser_failed", "Could not browse server files. Check SSH access and try again."); }
        finally { IsBusy = false; }
    }
    public string RepairPackageText => T("server_center.repair_package", "Server ZIP path (optional; same installed version, restores program files)");
    public string RepairPlanText => T("server_center.repair_plan", "Repair reapplies the current version's services and configuration, retaining the selected components, port, file permissions and TLS identity. Selected Guardian must pass authenticated IPC verification. Certificate regeneration and firewall changes below are explicit additional actions.");
    public string CurrentConfigurationTitle => T("server_center.configuration_current", "Current configuration");
    public string GuardianDescription => T("server_center.guardian_description", "Keep managed workloads running with process supervision. Applying this choice restarts services; removing the guardian stops its workloads while retaining definitions and logs.");
    public string NetworkSettingsTitle => T("server_center.network_settings", "Connection and startup");
    public string FileAccessDescription => T("server_center.file_access_description", "Choose which host files privileged operations can access. List directories only when selected-directory access is enabled.");
    public bool ShowConfigurationFileRoots => ConfigurationFileAccess?.Scope == ServerFileAccessScope.Whitelist;
    partial void OnConfigurationFileAccessChanged(FileAccessOption? value) => OnPropertyChanged(nameof(ShowConfigurationFileRoots));
    public IReadOnlyList<Services.ServerCenter.ServerInstallationDetail> CurrentConfigurationDetails
    {
        get
        {
            return [
                new(T("server_center.installation.listen_url", "Server listening address"), _reviewedConfiguration?.ListenUrl ?? "—"),
                Services.ServerCenter.ServerInstallationDetails.GuardianStatus(T("server_center.installation.guardian", "Process guardian"), _reviewedConfiguration?.GuardianEnabled, false, T),
                Services.ServerCenter.ServerInstallationDetails.GuardianStatus(T("server_center.guardian_running", "Guardian process running"), _reviewedConfiguration?.GuardianRunning, true, T)];
        }
    }
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
        OnPropertyChanged(nameof(CurrentConfigurationDetails));
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
