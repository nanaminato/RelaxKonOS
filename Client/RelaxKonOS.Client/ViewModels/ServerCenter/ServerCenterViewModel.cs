using System.Collections.ObjectModel;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.ViewModels.ServerCenter;

/// <summary>
/// Login-independent desktop host inventory. It keeps selectable SSH server-and-user targets;
/// credentials remain in platform secure storage and deployment remains a separate remote action.
/// </summary>
public partial class ServerCenterViewModel : ObservableObject
{
    private readonly IHostTargetStore _targets;
    private readonly IServerCenterConnectionResolver _connections;
    private readonly ISshHostKeyTrustStore _hostKeys;
    private readonly ISshCredentialStore _sshCredentials;
    private readonly SshDesktopSession _sshDesktop;
    private readonly IServerCenterReleaseSource _releaseSource;
    private readonly IServerCenterOperationJournal _operationJournal;
    private readonly LoginLocalizationService _localization;
    private ServerCenterHostKeyObservation? _pendingHostKey;
    private string? _selectedPlatformHostId;
    private bool _refreshingHostSelection;
    private readonly Dictionary<string, ServerHostSnapshotDto> _installationSnapshots = new();
    private readonly Dictionary<string, ServerHostProbeDto> _hostProbes = new();
    public string InstallationInfoTitle => T("server_center.installation.title", "Installed server details");
    public string RefreshActionDescription => T("login.local_manage_refresh_hint", "Check the current installation and service health");
    public string InstallActionDescription => T("login.local_manage_install_hint", "Choose a release package and review installation options");
    public string RepairActionDescription => T("login.local_manage_repair_hint", "Restore services, certificates and firewall rules");
    public string RollbackActionDescription => T("login.local_manage_rollback_hint", "Switch back to the last verified version");
    public string UninstallActionDescription => T("login.local_manage_uninstall_hint", "Remove Server; keep data by default");
    public string InstallationInfoNote => T("server_center.installation.note", "These facts reflect the SSH verification time. Use host preflight to refresh. Fields not returned by the host are shown as not provided.");
    public IReadOnlyList<ServerInstallationDetail> InstallationDetails => SelectedHost is { } host
        ? ServerInstallationDetails.Build(host, _installationSnapshots.GetValueOrDefault(host.HostId),
            _hostProbes.GetValueOrDefault(host.HostId), T)
        : Array.Empty<ServerInstallationDetail>();

    public ServerCenterViewModel(
        IHostTargetStore targets,
        IServerCenterConnectionResolver connections,
        ISshHostKeyTrustStore hostKeys,
        ISshCredentialStore sshCredentials,
        SshDesktopSession sshDesktop,
        IServerCenterReleaseSource releaseSource,
        IServerCenterOperationJournal operationJournal,
        LoginLocalizationService localization)
    {
        _targets = targets;
        _connections = connections;
        _hostKeys = hostKeys;
        _sshCredentials = sshCredentials;
        _sshDesktop = sshDesktop;
        _releaseSource = releaseSource;
        _operationJournal = operationJournal;
        _localization = localization;
        _localization.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    public ObservableCollection<ServerHostTarget> Hosts { get; } = [];
    public ObservableCollection<ServerCenterOperationRecord> Operations { get; } = [];
    public IReadOnlyList<HostPlatformOption> Platforms { get; } =
    [
        new(HostPlatformKind.Windows, "Windows"),
        new(HostPlatformKind.Linux, "Linux")
    ];
    public bool HasHosts => Hosts.Count > 0;
    public bool HasSelectedHost => SelectedHost is not null;
    public bool ShowMaintenanceSudoPassword => SelectedPlatform?.Platform == HostPlatformKind.Linux;
    public bool HasOperations => Operations.Count > 0;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private string _port = "22";
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _newHostPassword = string.Empty;
    [ObservableProperty] private bool _saveNewHostPassword = true;
    [ObservableProperty] private ServerHostTarget? _selectedHost;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _deploymentTransferProgress;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeploymentProgressIndeterminate))]
    private bool _isDeploymentTransferActive;
    public bool IsDeploymentProgressIndeterminate => !IsDeploymentTransferActive;
    [ObservableProperty] private bool _installationWizardOpen;
    [ObservableProperty] private bool _hasPreviousVersion;
    [ObservableProperty] private bool _hasIncompleteInstallation;
    [ObservableProperty] private string _maintenanceSudoPassword = string.Empty;
    [ObservableProperty] private bool _maintenanceAddFirewallRule;
    public string FirewallChoiceText => T("server_center.firewall_choice", "Add a firewall rule for the server TCP port");
    public string FirewallHelpText => T("server_center.firewall_help", "Only an enabled firewall is changed. If disabled, a notice is shown and no rule is added.");
    [ObservableProperty] private bool _repairLanCertificate;
    [ObservableProperty] private string _repairCertificateIdentities = "localhost,127.0.0.1";
    public string RepairCertificateText => T("server_center.repair_certificate", "Regenerate the LAN self-signed certificate during repair");
    public string RepairCertificateNote => T("server_center.repair_certificate_note", "Enter the current LAN IP or DNS names, separated by commas. Repair restarts the service; clients must trust the new certificate again.");
    public bool CanManageFirewall => HasManagedInstallation && SelectedHost?.LastVerified?.Mode is ServerInstallMode.WindowsSystem or ServerInstallMode.LinuxSystem;
    public bool HasManagedInstallation => SelectedHost?.LastVerified?.Installed == true;
    public string UpdateText => T("server_center.update", "Update RelaxKonOS");
    public string RecoverText => T("server_center.recover", "Recover installation");
    public string IncompleteInstallationText => T("server_center.incomplete_installation", "Services exist but the managed installation record is missing. Recover the installation before updating or uninstalling.");
    public string MaintenanceSudoPasswordText => T("server_center.wizard.sudo_password", "sudo password (leave blank to use the SSH password)");
    [ObservableProperty] private string _operationDiagnostics = string.Empty;
    public bool ShowWorkspaceProgress => IsBusy && !InstallationWizardOpen;
    public string OperationDetailsText => SelectedOperation is { } operation
        ? $"{operation.OperationId}\n{operation.Kind} · {operation.State} · {operation.Phase}\n{operation.ProblemCode}\n{operation.SafeMessage}\n{OperationDiagnostics}"
        : string.Empty;
    [ObservableProperty] private string _sshPassword = string.Empty;
    [ObservableProperty] private string _hostKeyFingerprint = string.Empty;
    [ObservableProperty] private bool _needsHostKeyConfirmation;

    /// <summary>本次核对是「替换已固定的密钥」，而不是「首次固定」。</summary>
    [ObservableProperty] private bool _hostKeyReplacesPinnedKey;

    [ObservableProperty] private bool _hostKeyChanged;
    [ObservableProperty] private string _previousHostKeyFingerprint = string.Empty;
    [ObservableProperty] private string _previousHostKeyConfirmedText = string.Empty;
    [ObservableProperty] private HostPlatformOption? _selectedPlatform;
    [ObservableProperty] private string _verifiedStateText = string.Empty;
    [ObservableProperty] private string _lastProbeText = string.Empty;
    [ObservableProperty] private bool _deleteServerData;
    [ObservableProperty] private bool _removeSmb;
    [ObservableProperty] private bool _removeNginx;
    [ObservableProperty] private bool _removeFrp;
    [ObservableProperty] private bool _removeMihomo;
    public string ComponentSelectionText => T("server_center.components_note", "Select components to remove. Unselected components and management data are retained. Docker containers and volumes are outside component cleanup; deleting data removes deployment records and configuration in the data root.");
    public string RemoveSmbText => T("server_center.remove_smb", "Remove managed SMB shares");
    public string RemoveNginxText => T("server_center.remove_nginx", "Remove managed Nginx");
    public string RemoveFrpText => T("server_center.remove_frp", "Remove managed FRP");
    public string RemoveMihomoText => T("server_center.remove_mihomo", "Remove managed Mihomo");
    private string SelectedComponents => string.Join(",", new[] { RemoveSmb ? "smb" : null, RemoveNginx ? "nginx" : null, RemoveFrp ? "frp" : null, RemoveMihomo ? "mihomo" : null }.Where(x => x is not null));
    [ObservableProperty] private ServerCenterOperationRecord? _selectedOperation;

    /// <summary>Workspace-owned modal presentation; the view model owns the deployment action only.</summary>
    public Func<Task>? ShowInstallationWizardAsync { get; set; }
    public Func<string, Task>? ShowManagementDialogAsync { get; set; }

    public string Title => T("server_center.title", "Server centre");
    public string Subtitle => T("server_center.subtitle", "Manage SSH hosts and RelaxKonOS server installations.");
    public string HostsPageTitle => T("server_center.page.hosts", "Hosts");
    public string DeploymentPageTitle => T("server_center.page.deployment", "Installation and maintenance");
    public string HistoryPageTitle => T("server_center.page.history", "Operation history");
    public string SelectHostHint => T("server_center.select_host_hint", "Select a server and user above.");
    public string HostsLabel => T("server_center.hosts", "Managed hosts");
    public string EmptyHostsText => T("server_center.empty", "No managed hosts have been added on this device.");
    public string AddHostLabel => T("server_center.add_host", "Add host");
    public string HostLabel => T("server_center.ssh_host", "SSH host");
    public string PortLabel => T("server_center.ssh_port", "Port");
    public string UserLabel => T("server_center.ssh_user", "SSH user");
    public string DisplayNameLabel => T("server_center.host_name", "Name (optional)");
    public string AddText => T("server_center.add", "Add host");
    public string RemoveText => T("server_center.remove", "Remove local host record");
    public string CloseText => T("common.close", "Close");
    public string CachedStateText => T("server_center.cached_state", "The status shown here is the last SSH verification, not a live health check.");
    public string SshPasswordLabel => T("server_center.ssh_password", "SSH password");
    public string NewHostPasswordLabel => T("server_center.new_host_password", "SSH password (optional)");
    public string SaveNewHostPasswordText => T("server_center.save_new_host_password", "Save this password securely on this device");
    public string DeploymentPasswordHint => T("server_center.deployment_password", "SSH password (leave blank to use the saved password)");
    public string SelectedTargetLabel => T("server_center.selected_target", "Server and user");
    // 首次固定与替换已固定的密钥共用一次核对，只有文案与是否需要并排展示旧指纹不同
    // （判定见 SshHostKeyReviewRules）。密钥变更曾经只是一行红字、没有任何出口——而 DHCP 地址漂移、
    // 克隆虚拟机或重装系统都会让指纹变化，用户必须有办法核对并接受新指纹后再继续。
    public string ConfirmHostKeyText => HostKeyReplacesPinnedKey
        ? T("server_center.host_key_replace_confirm", "Accept the new fingerprint")
        : T("server_center.confirm_host_key", "I verified this fingerprint");
    public string HostKeyReviewTitle => HostKeyReplacesPinnedKey
        ? T("server_center.host_key_replace_title", "SSH host key changed")
        : T("server_center.host_key_review_title", "Confirm SSH host key");
    public string HostKeyReviewText => HostKeyReplacesPinnedKey
        ? T("server_center.host_key_replace_message", "The fingerprint this host presented no longer matches the one saved on this device. A host rebuilt with new keys and another machine taking over this address under DHCP look exactly the same here. Accepting replaces the saved fingerprint; it is never replaced silently.")
        : T("server_center.host_key_review", "Verify this SSH host-key fingerprint with the host administrator before trusting it:");
    public string PinnedFingerprintLabel => T("server_center.pinned_fingerprint", "Fingerprint saved on this device");
    public string ObservedFingerprintLabel => T("server_center.observed_fingerprint", "Fingerprint this handshake presented");
    public string PlatformLabel => T("server_center.host_platform", "Host platform");
    public string ProbeText => T("server_center.probe", "Run host preflight");
    public string ProbeHelpText => T("server_center.probe_help", "Preflight uploads the fixed deployment launcher, reads OS, architecture, permissions and current installation status, then saves a timestamped SSH verification.");
    public string DeployText => SelectedHost?.LastVerified?.Installed == true
        ? T("server_center.update", "Install update")
        : T("server_center.install", "Install RelaxKonOS");
    public string RepairText => T("server_center.repair", "Repair current installation");
    public string RollbackText => T("server_center.rollback", "Restore previous version");
    public string UninstallText => T("server_center.uninstall", "Uninstall server");
    public string DeleteServerDataText => T("server_center.delete_data", "Also permanently delete managed data");
    public string OperationHistoryText => T("server_center.operation_history", "Operation history");
    public string LoadOperationHistoryText => T("server_center.load_operation_history", "Load operation history");
    public string ClearOperationHistoryText => T("server_center.clear_operation_history", "Clear completed records");
    public string RefreshOperationText => T("server_center.refresh_operation", "Refresh selected operation from host");
    public bool HasVerifiedState => !string.IsNullOrWhiteSpace(VerifiedStateText);
    public bool HasLastProbe => !string.IsNullOrWhiteSpace(LastProbeText);
    public string SelectedPlatformText => SelectedPlatform?.DisplayName ??
        T("server_center.platform_detecting", "Detecting host platform…");

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var loaded = await _targets.LoadAsync(cancellationToken).ConfigureAwait(true);
            var connected = _sshDesktop.Endpoint;
            if (connected is not null && !loaded.Any(host =>
                    string.Equals(host.HostId, ServerHostTargetRules.HostId(connected.Host, connected.Port, connected.UserName), StringComparison.Ordinal)))
            {
                var currentTarget = ServerHostTargetRules.Create(
                    connected.Host, connected.Port, connected.UserName, null, DateTimeOffset.UtcNow);
                await _targets.UpsertAsync(currentTarget, cancellationToken).ConfigureAwait(true);
                loaded = [.. loaded, currentTarget];
                StatusMessage = T("server_center.current_host_added", "The current SSH server and user were added to managed hosts.");
            }
            Hosts.Clear();
            foreach (var target in loaded.OrderByDescending(target => target.LastUsedAtUtc))
                Hosts.Add(target);
            SelectedHost = Hosts.FirstOrDefault(host => connected is not null &&
                string.Equals(host.SshHost, connected.Host, StringComparison.OrdinalIgnoreCase) &&
                host.SshPort == connected.Port && host.SshUserName == connected.UserName)
                ?? Hosts.FirstOrDefault();
            OnPropertyChanged(nameof(HasHosts));
        }
        catch (Exception)
        {
            ErrorMessage = T("server_center.load_failed", "Unable to read local host records.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddHostAsync(CancellationToken cancellationToken = default)
    {
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        if (!int.TryParse(Port, out var port) || !ServerHostTargetRules.IsValidEndpoint(Host, port, UserName))
        {
            ErrorMessage = T("server_center.invalid_host", "Enter a valid SSH host, port, and user.");
            return;
        }

        IsBusy = true;
        try
        {
            var target = ServerHostTargetRules.Create(Host, port, UserName, DisplayName, DateTimeOffset.UtcNow);
            var saved = await _targets.UpsertAsync(target, cancellationToken).ConfigureAwait(true);
            var passwordWasSaved = false;
            var passwordSaveFailed = false;
            if (SaveNewHostPassword && !string.IsNullOrEmpty(NewHostPassword))
            {
                var endpoint = ServerCenterSshEndpoint.Create(saved.SshHost, saved.SshPort, saved.SshUserName);
                passwordWasSaved = await _sshCredentials.SaveAsync(
                    SshCredentialRecord.From(endpoint, new ServerCenterSshCredential.Password(NewHostPassword), DateTimeOffset.UtcNow),
                    cancellationToken).ConfigureAwait(true) == SshCredentialSaveResult.Saved;
                passwordSaveFailed = !passwordWasSaved;
            }
            var incumbent = Hosts.FirstOrDefault(item => item.HostId == saved.HostId);
            if (incumbent is not null) Hosts.Remove(incumbent);
            Hosts.Insert(0, saved);
            OnPropertyChanged(nameof(HasHosts));
            SelectedHost = saved;
            Host = string.Empty;
            Port = "22";
            UserName = string.Empty;
            DisplayName = string.Empty;
            NewHostPassword = string.Empty;
            SaveNewHostPassword = true;
            StatusMessage = passwordWasSaved
                ? T("server_center.host_added_with_password", "Host and password were saved securely. Verify its SSH host key before deployment.")
                : passwordSaveFailed
                    ? T("server_center.host_added_password_not_saved", "Host record was saved, but the password could not be saved securely.")
                : T("server_center.host_added", "Host record saved. Add a password to use it for deployment.");
        }
        catch (Exception)
        {
            ErrorMessage = T("server_center.save_failed", "Unable to save the local host record.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveHost))]
    private async Task RemoveHostAsync(CancellationToken cancellationToken = default)
    {
        var target = SelectedHost;
        if (target is null) return;

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            if (await _targets.RemoveAsync(target.HostId, cancellationToken).ConfigureAwait(true))
            {
                Hosts.Remove(target);
                OnPropertyChanged(nameof(HasHosts));
                SelectedHost = null;
                StatusMessage = T("server_center.host_removed", "The local host record was removed. No server, data, login, or SSH credential was changed.");
            }
        }
        catch (Exception)
        {
            ErrorMessage = T("server_center.remove_failed", "Unable to remove the local host record.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRecover))]
    private Task RecoverAsync(CancellationToken cancellationToken = default) =>
        PerformInstalledOperationAsync(ServerDeploymentKind.Repair, ServerDataRetention.Retain, cancellationToken, recovering: true);

    private bool CanRecover() => CanProbeHost() && HasIncompleteInstallation && SelectedPlatform?.Platform == HostPlatformKind.Linux;

    private bool CanRemoveHost() => !IsBusy && SelectedHost is not null;

    [RelayCommand(CanExecute = nameof(CanMaintain))]
    private Task RepairAsync(CancellationToken cancellationToken = default) =>
        PerformInstalledOperationAsync(ServerDeploymentKind.Repair, ServerDataRetention.Retain, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanRollback))]
    private Task RollbackAsync(CancellationToken cancellationToken = default) =>
        PerformInstalledOperationAsync(ServerDeploymentKind.Rollback, ServerDataRetention.Retain, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private Task UninstallAsync(CancellationToken cancellationToken = default) =>
        PerformInstalledOperationAsync(
            ServerDeploymentKind.Uninstall,
            DeleteServerData ? ServerDataRetention.Delete : ServerDataRetention.Retain,
            cancellationToken);

    [RelayCommand(CanExecute = nameof(CanLoadOperationHistory))]
    private async Task LoadOperationHistoryAsync(CancellationToken cancellationToken = default)
    {
        var target = SelectedHost;
        if (target is null) return;
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await ReloadOperationHistoryAsync(target.HostId, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception)
        {
            ErrorMessage = T("server_center.history_load_failed", "Unable to read the local operation history.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadOperationHistory))]
    private async Task ClearOperationHistoryAsync(CancellationToken cancellationToken = default)
    {
        var target = SelectedHost;
        if (target is null) return;
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await _operationJournal.ClearCompletedAsync(target.HostId, cancellationToken).ConfigureAwait(true);
            if (SelectedHost?.HostId == target.HostId)
            {
                SelectedOperation = null;
                await ReloadOperationHistoryAsync(target.HostId, cancellationToken).ConfigureAwait(true);
            }
            StatusMessage = T("server_center.history_cleared", "Completed local records were cleared; unfinished operations and server receipts were retained.");
        }
        catch (Exception)
        {
            ErrorMessage = T("server_center.history_clear_failed", "Unable to clear local operation records.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRefreshOperation))]
    private async Task RefreshOperationAsync(CancellationToken cancellationToken = default)
    {
        var target = SelectedHost;
        var platform = SelectedPlatform;
        var record = SelectedOperation;
        if (target is null || platform is null || record is null) return;

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var tools = await _releaseSource.ResolveToolsAsync(platform.Platform, cancellationToken).ConfigureAwait(true);
            if (tools is null)
            {
                ErrorMessage = T("server_center.install_script_unavailable", "The bundled installation script is unavailable. Rebuild or reinstall this client.");
                return;
            }
            var credential = await ResolveCredentialAsync(target, cancellationToken).ConfigureAwait(true);
            if (credential is null) return;
            await using var session = await _connections.ConnectAsync(
                target.HostId,
                credential,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(true);
            await using var launcher = tools.OpenLauncher();

            var client = new ServerCenterDeploymentClient(session.Transport);
            var staged = await client.StageQueryAsync(record.OperationId, platform.Platform, launcher, cancellationToken)
                .ConfigureAwait(true);
            var receipt = await client.QueryAsync(staged, cancellationToken).ConfigureAwait(true);
            var diagnostics = await client.ReadDiagnosticsAsync(staged, cancellationToken).ConfigureAwait(true);
            if (credential is ServerCenterSshCredential.Password password && !string.IsNullOrEmpty(password.Secret))
                diagnostics = diagnostics.Replace(password.Secret, "[redacted]", StringComparison.Ordinal);
            await _operationJournal.RecordAsync(ServerCenterOperationRecord.From(target.HostId, receipt), cancellationToken)
                .ConfigureAwait(true);

            if (receipt.Snapshot is not null)
                await ApplySnapshotAsync(target, receipt.Snapshot, cancellationToken).ConfigureAwait(true);
            StatusMessage = T("server_center.operation_refreshed", "The selected operation receipt was refreshed from the host.");
            await ReloadOperationHistoryAsync(target.HostId, cancellationToken).ConfigureAwait(true);
            SelectedOperation = Operations.FirstOrDefault(item => item.OperationId == record.OperationId);
            OperationDiagnostics = string.IsNullOrWhiteSpace(diagnostics)
                ? T("server_center.diagnostics_empty", "The host returned no deployment log. This older operation may have no saved diagnostics; retry installation with the updated client.")
                : diagnostics;
        }
        catch (ServerCenterHostKeyRejectedException rejected)
        {
            await ApplyHostKeyRejectionAsync(rejected, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = string.Empty;
        }
        catch (Exception)
        {
            ErrorMessage = T("server_center.operation_refresh_failed", "The remote operation receipt could not be refreshed. Check SSH access and try again.");
            OperationDiagnostics = T("server_center.diagnostics_failed", "Could not read the host deployment log. Check SSH access and refresh this operation again.");
        }
        finally
        {
            SshPassword = string.Empty;
            IsBusy = false;
        }
    }

    private async Task PerformInstalledOperationAsync(
        ServerDeploymentKind kind,
        ServerDataRetention retention,
        CancellationToken cancellationToken, bool recovering = false)
    {
        var target = SelectedHost;
        var platform = SelectedPlatform;
        if (target is null || platform is null) return;

        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = T("server_center.progress.connecting", "Connecting to the server…");
        try
        {
            var tools = await _releaseSource.ResolveToolsAsync(platform.Platform, cancellationToken).ConfigureAwait(true);
            if (tools is null)
            {
                ErrorMessage = T("server_center.install_script_unavailable", "The bundled installation script is unavailable. Rebuild or reinstall this client.");
                return;
            }

            var credential = await ResolveCredentialAsync(target, cancellationToken).ConfigureAwait(true);
            if (credential is null) return;
            await using var session = await _connections.ConnectAsync(
                target.HostId,
                credential,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(true);
            StatusMessage = T("server_center.progress.checking", "Checking the installation environment…");
            var probeReceipt = await ExecuteReadOnlyAsync(session, tools, ServerDeploymentKind.Probe, null, cancellationToken).ConfigureAwait(true);
            var probe = probeReceipt.Probe;
            var sudoPassword = platform.Platform == HostPlatformKind.Linux && probe?.Elevated == false && probe.SudoAvailable && probe.ExistingMode != ServerInstallMode.LinuxUser
                ? (!string.IsNullOrEmpty(MaintenanceSudoPassword) ? MaintenanceSudoPassword : (credential as ServerCenterSshCredential.Password)?.Secret ?? "") : null;
            if (sudoPassword is not null)
            {
                probeReceipt = await ExecuteReadOnlyAsync(session, tools, ServerDeploymentKind.Probe, null, cancellationToken, sudoPassword).ConfigureAwait(true);
                probe = probeReceipt.Probe;
            }
            if (probe is null || probe.RuntimeIdentifier is null || recovering && !probe.OsSupported || !PlatformMatches(platform.Platform, probe.HostPlatform) ||
                (recovering ? probe.ExistingInstalled || platform.Platform != HostPlatformKind.Linux :
                !probe.ExistingInstalled || probe.ExistingMode is null || !ServerInstallationId.IsValid(probe.ExistingInstallationId)))
            {
                ErrorMessage = T("server_center.maintenance_preflight_failed", "The host no longer reports a supported managed installation. This operation is blocked.");
                return;
            }

            var operationMode = recovering ? ServerInstallMode.LinuxSystem : probe.ExistingMode!.Value;
            var rotateCertificate = kind == ServerDeploymentKind.Repair && !recovering && RepairLanCertificate;
            if (rotateCertificate && (operationMode == ServerInstallMode.LinuxUser ||
                string.IsNullOrWhiteSpace(RepairCertificateIdentities)))
            {
                ErrorMessage = T("server_center.repair_certificate_invalid", "Certificate repair requires a system-service installation and at least one IP or DNS name.");
                return;
            }
            var request = new ServerDeploymentRequest(
                ServerDeploymentProtocol.Version,
                Guid.NewGuid(),
                kind,
                new ServerDeploymentOptions(
                    ServerPackageSourceKind.OfficialStable,
                    ServerNetworkProfile.Loopback,
                    retention,
                    operationMode,
                    null,
                    null,
                    null,
                    null,
                    probe.ExistingInstallationId,
                    null,
                    CertificateMode: rotateCertificate ? ServerCertificateMode.SelfSigned : null,
                    SelfSignedIdentities: rotateCertificate ? RepairCertificateIdentities.Trim() : null,
                    Confirmed: true,
                    Language: _localization.CurrentLanguage,
                    AllowUnsupportedSystem: operationMode == ServerInstallMode.LinuxSystem && !probe.OsSupported,
                    AddFirewallRule: kind == ServerDeploymentKind.Repair && !recovering && operationMode != ServerInstallMode.LinuxUser && MaintenanceAddFirewallRule,
                    RemoveComponents: kind == ServerDeploymentKind.Uninstall && operationMode != ServerInstallMode.LinuxUser ? SelectedComponents : ""));
            var receipt = await ExecuteFixedOperationAsync(session, tools, request, cancellationToken, sudoPassword).ConfigureAwait(true);

            // Read the separate status receipt even after uninstall. The install identity is retained
            // locally only as a stable association for preserved data; it is never treated as live API health.
            StatusMessage = T("server_center.progress.verifying", "Verifying the operation result…");
            var status = await ExecuteReadOnlyAsync(
                session, tools, ServerDeploymentKind.Status, StatusOptions(operationMode), cancellationToken, sudoPassword).ConfigureAwait(true);
            if (status.Snapshot is null)
            {
                ErrorMessage = T("server_center.status_missing", "The deployment finished, but no authoritative SSH-side status receipt was returned.");
                return;
            }

            await ApplySnapshotAsync(target, status.Snapshot, cancellationToken).ConfigureAwait(true);
            HasIncompleteInstallation = false;
            LastProbeText = FormatProbe(probe);
            if (receipt.State != ServerDeploymentState.Succeeded)
            {
                ErrorMessage = string.Format(
                    T("server_center.maintenance_receipt_failed", "The operation did not succeed: {0}"),
                    receipt.SafeMessage ?? receipt.ProblemCode ?? receipt.State.ToString());
                return;
            }
            if (status.State != ServerDeploymentState.Succeeded ||
                (kind == ServerDeploymentKind.Uninstall ? status.Snapshot.Installed :
                    !status.Snapshot.Installed || !status.Snapshot.Healthy))
            {
                ErrorMessage = T("server_center.maintenance_verification_failed", "The host state after the operation does not confirm success. Check the operation record.");
                return;
            }
            StatusMessage = kind switch
            {
                ServerDeploymentKind.Repair => T("server_center.repair_succeeded", "The current installation was repaired and verified through SSH."),
                ServerDeploymentKind.Rollback => T("server_center.rollback_succeeded", "The previous program version was restored and verified through SSH."),
                ServerDeploymentKind.Uninstall when retention == ServerDataRetention.Retain => T("server_center.uninstall_retained_succeeded", "The server was uninstalled. Managed data was retained on the host."),
                ServerDeploymentKind.Uninstall => T("server_center.uninstall_deleted_succeeded", "The server and managed data were uninstalled."),
                _ => receipt.SafeMessage ?? T("server_center.operation_succeeded", "The server operation completed.")
            };
            if (receipt.Result?.FirewallStatus == "disabled") StatusMessage += " " + T("server_center.firewall_disabled", "The host firewall is disabled; no rule was added.");
            if (receipt.Result?.FirewallStatus == "ruleAdded") StatusMessage += " " + T("server_center.firewall_added", "The server TCP port firewall rule was added.");
        }
        catch (ServerCenterHostKeyRejectedException rejected)
        {
            await ApplyHostKeyRejectionAsync(rejected, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = string.Empty;
        }
        catch (Exception)
        {
            ErrorMessage = T("server_center.maintenance_failed", "The server operation could not be completed. Its SSH-side receipt can be checked from this host later.");
        }
        finally
        {
            SshPassword = string.Empty;
            MaintenanceAddFirewallRule = false;
        DeleteServerData = false;
        RemoveSmb = RemoveNginx = RemoveFrp = RemoveMihomo = false;
            if (HasError) StatusMessage = string.Empty;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenInstallationWizard))]
    private async Task OpenInstallationWizardAsync()
    {
        InstallationWizardOpen = true;
        try { if (ShowInstallationWizardAsync is not null) await ShowInstallationWizardAsync(); }
        finally { InstallationWizardOpen = false; }
    }
    partial void OnInstallationWizardOpenChanged(bool value) => OnPropertyChanged(nameof(ShowWorkspaceProgress));
    [RelayCommand(CanExecute = nameof(CanMaintain))]
    private Task UpdateAsync() => OpenInstallationWizardAsync();
    partial void OnOperationDiagnosticsChanged(string value) => OnPropertyChanged(nameof(OperationDetailsText));

    public async Task<bool> DeployAsync(
        ServerInstallationOptions installation, CancellationToken cancellationToken = default)
    {
        var target = SelectedHost;
        var platform = SelectedPlatform;
        if (target is null || platform is null) return false;

        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = T("server_center.progress.connecting", "Connecting to the server…");
        string? convertedCertificate = null;
        var deploymentStage = "SSH";
        IsDeploymentTransferActive = false;
        try
        {
            var tools = await _releaseSource.ResolveToolsAsync(platform.Platform, cancellationToken).ConfigureAwait(true);
            if (tools is null)
            {
                ErrorMessage = T("server_center.install_script_unavailable", "The bundled installation script is unavailable. Rebuild or reinstall this client.");
                return false;
            }

            var credential = await ResolveCredentialAsync(target, cancellationToken).ConfigureAwait(true);
            if (credential is null) return false;
            await using var session = await _connections.ConnectAsync(
                target.HostId,
                credential,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(true);
            StatusMessage = T("server_center.progress.checking", "Checking the installation environment…");
            var probeReceipt = await ExecuteReadOnlyAsync(session, tools, ServerDeploymentKind.Probe, null, cancellationToken).ConfigureAwait(true);
            var probe = probeReceipt.Probe;
            await _operationJournal.RecordAsync(ServerCenterOperationRecord.From(target.HostId, probeReceipt), cancellationToken)
                .ConfigureAwait(true);
            if (probe is null)
            {
                ErrorMessage = probeReceipt.SafeMessage ?? T("server_center.probe_missing", "The host preflight returned no system information. Check the operation record.");
                return false;
            }
            LastProbeText = FormatProbe(probe);
            if (!PlatformMatches(platform.Platform, probe.HostPlatform))
            {
                ErrorMessage = string.Format(T("server_center.platform_mismatch", "Previously detected platform: {0}; SSH host now reports {1}. Run host detection again."), platform.Platform, probe.HostPlatform);
                return false;
            }
            if (probe.RuntimeIdentifier is null)
            {
                ErrorMessage = string.Format(T("server_center.architecture_unsupported", "The host CPU architecture {0} is unsupported. Supported architectures: x86_64 and arm64."), probe.Architecture);
                return false;
            }
            if (!probe.OsSupported && !installation.AllowUnsupportedSystem)
            {
                ErrorMessage = string.Format(T("server_center.os_unsupported", "The host reports {0} {1} ({2}). Supported Linux systems: Debian 12, Ubuntu 22.04/24.04/26.04."),
                    probe.OsId ?? "?", probe.OsVersion ?? "?", probe.RuntimeIdentifier);
                return false;
            }

            var runtime = probe.RuntimeIdentifier.Value;
            var mode = installation.Mode ?? RecommendedMode(platform.Platform, probe);
            if (mode is null)
            {
                ErrorMessage = T("server_center.elevation_required", "The selected installation mode requires an elevated SSH session or approved sudo access.");
                return false;
            }
            if (!CanUseInstallationMode(platform.Platform, probe, mode.Value))
            {
                ErrorMessage = T("server_center.install_mode_unavailable", "The selected installation mode is not available for this SSH session.");
                return false;
            }
            var sudoPassword = platform.Platform == HostPlatformKind.Linux && mode == ServerInstallMode.LinuxSystem && !probe.Elevated
                ? (!string.IsNullOrEmpty(installation.SudoPassword) ? installation.SudoPassword
                    : (credential as ServerCenterSshCredential.Password)?.Secret ?? string.Empty)
                : null;
            if (sudoPassword is not null)
            {
                deploymentStage = "sudo";
                probeReceipt = await ExecuteReadOnlyAsync(session, tools, ServerDeploymentKind.Probe, null,
                    cancellationToken, sudoPassword).ConfigureAwait(true);
                if (probeReceipt.State != ServerDeploymentState.Succeeded || probeReceipt.Probe is null ||
                    probeReceipt.Probe.RuntimeIdentifier != runtime || (!probeReceipt.Probe.OsSupported && !installation.AllowUnsupportedSystem))
                {
                    ErrorMessage = probeReceipt.SafeMessage ?? T("server_center.sudo_failed", "sudo authentication failed. Check the sudo password and account permissions.");
                    return false;
                }
                probe = probeReceipt.Probe;
            }
            if (installation.CertificateMode == ServerCertificateMode.Custom && mode == ServerInstallMode.LinuxUser)
            {
                ErrorMessage = T("server_center.certificate_mode_unavailable", "A custom TLS certificate requires a system-service installation.");
                return false;
            }

            var kind = probe.ExistingInstalled ? ServerDeploymentKind.Upgrade : ServerDeploymentKind.Install;
            if (kind == ServerDeploymentKind.Upgrade && !ServerInstallationId.IsValid(probe.ExistingInstallationId))
            {
                ErrorMessage = T("server_center.installation_identity_missing", "The existing installation did not report a valid managed installation identity. Update is blocked.");
                return false;
            }

            StatusMessage = T("server_center.progress.preparing", "Preparing and uploading installation files…");
            ServerCenterReleaseAssets? release = null;
            if (installation.Source == ServerPackageSourceKind.LocalBundle)
            {
                if (!string.IsNullOrWhiteSpace(installation.LocalBundlePath))
                    release = await _releaseSource.ResolveLocalBundleAsync(platform.Platform,
                        runtime, mode.Value, installation.LocalBundlePath, cancellationToken).ConfigureAwait(true);
                if (release is null)
                {
                    ErrorMessage = T("server_center.local_bundle_unavailable", "Choose an available ZIP release bundle.");
                    return false;
                }
            }
            if (installation.Source == ServerPackageSourceKind.RemoteBundle &&
                (string.IsNullOrWhiteSpace(installation.RemoteBundlePath) ||
                 !installation.RemoteBundlePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            {
                ErrorMessage = T("server_center.remote_bundle_unavailable", "Choose a ZIP release bundle from this SSH server.");
                return false;
            }
            if (!HasUsableCertificate(installation))
            {
                ErrorMessage = T("server_center.certificate_unavailable", "The selected certificate files are unavailable.");
                return false;
            }

            if (installation.CertificateMode == ServerCertificateMode.Custom &&
                installation.CertificateFormat == ServerCertificateFormat.Pem)
                convertedCertificate = await ConvertPemCertificateAsync(installation, cancellationToken).ConfigureAwait(true);

            var request = new ServerDeploymentRequest(
                ServerDeploymentProtocol.Version,
                Guid.NewGuid(),
                kind,
                new ServerDeploymentOptions(
                    installation.Source,
                    installation.Network,
                    ServerDataRetention.Retain,
                    mode,
                    null,
                    installation.PackageUri,
                    release?.StagedPackageName,
                    installation.PackageDigest,
                    kind == ServerDeploymentKind.Upgrade ? probe.ExistingInstallationId : null,
                    installation.ServerPort,
                    mode == ServerInstallMode.LinuxUser ? ServerFileAccessScope.Restricted : installation.FileAccess,
                    installation.CertificateMode,
                    installation.SelfSignedIdentities,
                    Confirmed: true, RemotePackagePath: installation.RemoteBundlePath,
                    Language: _localization.CurrentLanguage, ReleaseCatalogBaseUri: installation.ReleaseCatalogBaseUri,
                    InstallRoot: mode == ServerInstallMode.LinuxUser ? null : installation.InstallRoot, DataRoot: installation.DataRoot,
                    ConfigRoot: mode == ServerInstallMode.LinuxUser ? installation.ConfigRoot : null,
                    StateRoot: mode == ServerInstallMode.LinuxUser ? installation.StateRoot : null,
                    CacheRoot: mode == ServerInstallMode.LinuxUser ? installation.CacheRoot : null,
                    FileRoots: mode == ServerInstallMode.LinuxUser ? null : installation.FileRoots,
                    AdministratorFileAccess: mode == ServerInstallMode.LinuxSystem ? installation.AdministratorFileAccess : null,
                    AdministratorFileRoots: mode == ServerInstallMode.LinuxSystem ? installation.AdministratorFileRoots : null,
                    RootFileAccess: mode == ServerInstallMode.LinuxSystem ? installation.RootFileAccess : null,
                    RootFileRoots: mode == ServerInstallMode.LinuxSystem ? installation.RootFileRoots : null,
                    DockerAccess: mode == ServerInstallMode.LinuxSystem && installation.DockerAccess,
                    AllowUnsupportedSystem: platform.Platform == HostPlatformKind.Linux && installation.AllowUnsupportedSystem,
                    AddFirewallRule: mode != ServerInstallMode.LinuxUser && installation.AddFirewallRule));
            await using var launcher = tools.OpenLauncher();

            await using var archive = release?.OpenArchive();
            var client = new ServerCenterDeploymentClient(session.Transport);
            await using var certificate = installation.CertificateMode == ServerCertificateMode.Custom
                ? File.OpenRead(convertedCertificate ?? installation.CertificatePath!) : null;
            deploymentStage = "SFTP";
            var staged = await client.StageAsync(
                request, platform.Platform, launcher, archive, runtime,
                certificate, installation.CertificatePassword, cancellationToken,
                new Progress<double>(fraction => {
                    if (deploymentStage != "SFTP") return;
                    IsDeploymentTransferActive = true;
                    DeploymentTransferProgress = fraction * 100;
                    StatusMessage =
                    T("server_center.progress.preparing", "Preparing and uploading installation files…") +
                    $" {fraction:P0} · {(long)(fraction * (archive?.Length ?? 0)):N0} / {archive?.Length ?? 0:N0} B"; })).ConfigureAwait(true);
            deploymentStage = "install";
            IsDeploymentTransferActive = false;
            StatusMessage = kind == ServerDeploymentKind.Upgrade
                ? T("server_center.progress.upgrading", "Updating, please wait…")
                : T("server_center.progress.installing", "Installing, please wait…");
            var receipt = await client.ExecuteAsync(staged, cancellationToken, sudoPassword,
                new Progress<ServerDeploymentTransfer?>(transfer => {
                    if (deploymentStage != "install") return;
                    IsDeploymentTransferActive = transfer?.Total is > 0;
                    DeploymentTransferProgress = transfer?.Total is > 0 ? Math.Clamp(transfer.Bytes * 100d / transfer.Total.Value, 0, 100) : 0;
                    StatusMessage = transfer is null
                        ? kind == ServerDeploymentKind.Upgrade ? T("server_center.progress.upgrading", "Updating, please wait…")
                            : T("server_center.progress.installing", "Installing, please wait…")
                        : T("server_center.progress.downloading", "Downloading the installation package…") +
                            (transfer.Total is > 0 ? $" {(double)transfer.Bytes / transfer.Total.Value:P0} · {transfer.Bytes:N0} / {transfer.Total:N0} B" : $" {transfer.Bytes:N0} B");
                })).ConfigureAwait(true);
            await _operationJournal.RecordAsync(ServerCenterOperationRecord.From(target.HostId, receipt), cancellationToken)
                .ConfigureAwait(true);

            if (receipt.State != ServerDeploymentState.Succeeded)
            {
                ErrorMessage = receipt.SafeMessage ?? T("server_center.install_failed", "Installation failed. Check the operation record.");
                return false;
            }

            // A successful launcher process is only a transport result.  Read a separate SSH-side
            // status receipt before declaring success or refreshing the local cached state.
            deploymentStage = "status";
            IsDeploymentTransferActive = false;
            StatusMessage = T("server_center.progress.verifying", "Verifying the operation result…");
            var status = await ExecuteReadOnlyAsync(
                session, tools, ServerDeploymentKind.Status, StatusOptions(mode.Value), cancellationToken, sudoPassword).ConfigureAwait(true);
            if (status.Snapshot is null)
            {
                ErrorMessage = T("server_center.status_missing", "The deployment finished, but no authoritative SSH-side status receipt was returned.");
                return false;
            }
            await ApplySnapshotAsync(target, status.Snapshot, cancellationToken).ConfigureAwait(true);
            LastProbeText = FormatProbe(probe);
            StatusMessage = kind == ServerDeploymentKind.Install
                ? T("server_center.install_succeeded", "RelaxKonOS was installed and verified through SSH. Return to the login window to sign in.")
                : T("server_center.update_succeeded", "RelaxKonOS was updated and verified through SSH. Sign in again if the existing session was interrupted.");
            if (receipt.Result?.FirewallStatus == "disabled")
                StatusMessage += " " + T("server_center.firewall_disabled", "The host firewall is disabled; no firewall rule was added.");
            else if (receipt.Result?.FirewallStatus == "ruleAdded")
                StatusMessage += " " + T("server_center.firewall_added", "The server TCP port was allowed through the firewall.");
            return true;
        }
        catch (ServerCenterHostKeyRejectedException rejected)
        {
            await ApplyHostKeyRejectionAsync(rejected, cancellationToken).ConfigureAwait(true);
            return false;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = string.Empty;
            return false;
        }
        catch (Exception error)
        {
            // Never surface arbitrary exception messages: transport errors can contain credentials.
            ErrorMessage = string.Format(T("server_center.deploy_failed_detail", "Server operation failed at {0} ({1}). Check the operation record."),
                deploymentStage, error.GetType().Name);
            return false;
        }
        finally
        {
            if (convertedCertificate is not null)
            {
                try { File.Delete(convertedCertificate); }
                catch (IOException) { }
            }
            SshPassword = string.Empty;
            deploymentStage = "done";
            IsDeploymentTransferActive = false;
            if (HasError) StatusMessage = string.Empty;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanProbeHost))]
    private async Task ProbeHostAsync(CancellationToken cancellationToken = default)
    {
        var target = SelectedHost;
        if (target is null) return;
        if (SelectedPlatform is null)
            await EnsureSelectedHostPlatformAsync(cancellationToken).ConfigureAwait(true);
        var platform = SelectedPlatform;
        if (platform is null) return;

        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        LastProbeText = string.Empty;
        try
        {
            var tools = await _releaseSource.ResolveToolsAsync(platform.Platform, cancellationToken).ConfigureAwait(true);
            if (tools is null)
            {
                ErrorMessage = T("server_center.install_script_unavailable", "The bundled installation script is unavailable. Rebuild or reinstall this client.");
                return;
            }

            var credential = await ResolveCredentialAsync(target, cancellationToken).ConfigureAwait(true);
            if (credential is null) return;
            await using var session = await _connections.ConnectAsync(
                target.HostId,
                credential,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(true);
            var probe = await ExecuteReadOnlyAsync(session, tools, ServerDeploymentKind.Probe, null, cancellationToken).ConfigureAwait(true);
            var sudoPassword = platform.Platform == HostPlatformKind.Linux && probe.Probe?.Elevated == false && probe.Probe.SudoAvailable && probe.Probe.ExistingMode != ServerInstallMode.LinuxUser
                ? (!string.IsNullOrEmpty(MaintenanceSudoPassword) ? MaintenanceSudoPassword : (credential as ServerCenterSshCredential.Password)?.Secret ?? "") : null;
            if (sudoPassword is not null)
                probe = await ExecuteReadOnlyAsync(session, tools, ServerDeploymentKind.Probe, null, cancellationToken, sudoPassword).ConfigureAwait(true);
            if (probe.Probe is null) { ErrorMessage = probe.SafeMessage ?? T("server_center.sudo_failed", "sudo authentication failed."); return; }

            // Status is separate from reachability/preflight.  Query it in the same trusted session
            // so a host with a stopped API is not incorrectly offered a reinstall.
            var probeFacts = probe.Probe;
            var statusMode = probeFacts is null ? null : sudoPassword is not null && probeFacts.ExistingMode is null
                ? ServerInstallMode.LinuxSystem : StatusMode(platform.Platform, probeFacts);
            if (statusMode is null)
            {
                ErrorMessage = T("server_center.status_mode_missing", "The host did not report an installation mode that can be checked safely.");
                return;
            }
            var status = await ExecuteReadOnlyAsync(
                session, tools, ServerDeploymentKind.Status, StatusOptions(statusMode.Value), cancellationToken, sudoPassword).ConfigureAwait(true);
            HasPreviousVersion = !string.IsNullOrWhiteSpace(status.Snapshot?.PreviousVersion);
            HasIncompleteInstallation = status.Snapshot?.Installed != true && platform.Platform == HostPlatformKind.Linux &&
                (await session.Transport.RunAsync("systemctl is-active --quiet relaxkonos-server.service", cancellationToken).ConfigureAwait(true)).Succeeded;
            if (status.Snapshot is not null)
            {
                await ApplySnapshotAsync(target, status.Snapshot, cancellationToken).ConfigureAwait(true);
            }
            if (probe.Probe is not null)
                LastProbeText = FormatProbe(probe.Probe);
            StatusMessage = T("server_center.probe_succeeded", "Host preflight and SSH-side status check completed.");
        }
        catch (ServerCenterHostKeyRejectedException rejected)
        {
            await ApplyHostKeyRejectionAsync(rejected, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = string.Empty;
        }
        catch (Exception)
        {
            ErrorMessage = T("server_center.probe_failed", "Host preflight could not be completed. Check SSH access and retry host detection.");
        }
        finally
        {
            SshPassword = string.Empty;
            IsBusy = false;
        }
    }

    /// <summary>Detects the selected SSH host's OS when the installation page becomes active.</summary>
    public async Task EnsureSelectedHostPlatformAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy || SelectedHost is null || SelectedPlatform is not null || HostKeyChanged) return;

        var target = SelectedHost;
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var credential = await ResolveCredentialAsync(target, cancellationToken).ConfigureAwait(true);
            if (credential is null) return;
            await using var session = await _connections.ConnectAsync(
                target.HostId, credential, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(true);
            var platform = await DetectHostPlatformAsync(session, cancellationToken).ConfigureAwait(true);
            if (platform is null)
            {
                ErrorMessage = T("server_center.platform_detection_failed", "The SSH host did not identify itself as a supported Linux or Windows host.");
                return;
            }
            SelectedPlatform = Platforms.Single(option => option.Platform == platform.Value);
            StatusMessage = string.Format(T("server_center.platform_detected", "Detected host platform: {0}."), SelectedPlatform.DisplayName);
        }
        catch (ServerCenterHostKeyRejectedException rejected)
        {
            await ApplyHostKeyRejectionAsync(rejected, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = string.Empty;
        }
        catch (Exception)
        {
            ErrorMessage = T("server_center.platform_detection_failed", "The SSH host did not identify itself as a supported Linux or Windows host.");
        }
        finally
        {
            SshPassword = string.Empty;
            IsBusy = false;
        }
    }

    /// <summary>Returns only parsed IP addresses from the selected, trusted SSH host.</summary>
    public async Task<IReadOnlyList<string>?> GetHostIpAddressesAsync(CancellationToken cancellationToken = default)
    {
        var target = SelectedHost;
        if (target is null || IsBusy) return null;

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var credential = await ResolveCredentialAsync(target, cancellationToken).ConfigureAwait(true);
            if (credential is null) return null;
            await using var session = await _connections.ConnectAsync(
                target.HostId, credential, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(true);
            var platform = SelectedPlatform?.Platform ?? await DetectHostPlatformAsync(session, cancellationToken).ConfigureAwait(true);
            if (platform is null)
            {
                ErrorMessage = T("server_center.platform_detection_failed", "The SSH host did not identify itself as a supported Linux or Windows host.");
                return null;
            }
            SelectedPlatform ??= Platforms.Single(option => option.Platform == platform.Value);
            var command = platform == HostPlatformKind.Windows
                ? "powershell.exe -NoProfile -NonInteractive -Command \"Get-NetIPAddress -AddressFamily IPv4,IPv6 | ForEach-Object { $_.IPAddress }\""
                : "hostname -I";
            var result = await session.Transport.RunAsync(command, cancellationToken).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                ErrorMessage = T("server_center.host_addresses_failed", "Unable to read IP addresses from this SSH host.");
                return null;
            }
            return result.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Where(address => IPAddress.TryParse(address, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(address => address, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (ServerCenterHostKeyRejectedException rejected)
        {
            await ApplyHostKeyRejectionAsync(rejected, cancellationToken).ConfigureAwait(true);
            return null;
        }
        catch (Exception)
        {
            ErrorMessage = T("server_center.host_addresses_failed", "Unable to read IP addresses from this SSH host.");
            return null;
        }
        finally
        {
            SshPassword = string.Empty;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirmHostKey))]
    private async Task ConfirmHostKeyAsync(CancellationToken cancellationToken = default)
    {
        var target = SelectedHost;
        var observation = _pendingHostKey;
        if (target is null || observation is null || !NeedsHostKeyConfirmation) return;

        var endpoint = ServerCenterSshEndpoint.Create(target.SshHost, target.SshPort, target.SshUserName);
        if (!string.Equals(endpoint.Host, ServerHostTrustRules.NormalizeHost(observation.Host), StringComparison.Ordinal) ||
            endpoint.Port != observation.Port)
        {
            ErrorMessage = T("server_center.host_key_mismatch", "The observed host key does not belong to the selected host.");
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            // 首次固定与替换已固定的密钥在这里是同一个动作：用户已经看过（替换时还包括旧指纹）
            // 并显式按下确认，Replace 语义由信任仓库保证同端点同算法只留一条。
            await _hostKeys.TrustAsync(endpoint, observation, cancellationToken).ConfigureAwait(true);
            ClearPendingHostKey();
            StatusMessage = T("server_center.host_key_trusted", "Host key saved. Retry the deployment action.");
        }
        catch (Exception)
        {
            ErrorMessage = T("server_center.host_key_save_failed", "Unable to save the SSH host key.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 把一次被拒绝的握手翻译成「还差哪一步」，并在密钥变更时把被取代的那条固定记录一并取出。
    /// 旧指纹必须与新指纹同时交给用户：只看新指纹无法区分「重装/重建过的同一台机器」和
    /// 「这个地址被另一台机器接管」（判定见 <see cref="SshHostKeyReviewRules"/>）。
    /// </summary>
    private async Task ApplyHostKeyRejectionAsync(
        ServerCenterHostKeyRejectedException rejected, CancellationToken cancellationToken)
    {
        var observation = rejected.Observation;
        ServerHostKeyRecord? previous = null;
        if (rejected.Trust == ServerHostKeyTrust.Changed)
        {
            var known = await _hostKeys.LoadAsync(cancellationToken).ConfigureAwait(true);
            previous = ServerHostTrustRules.Find(known, observation.Host, observation.Port, observation.Algorithm);
        }

        var review = SshHostKeyReviewRules.Plan(rejected.Trust, observation, previous);
        _pendingHostKey = observation;
        NeedsHostKeyConfirmation = review is not null;
        HostKeyReplacesPinnedKey = review?.ReplacesPinnedKey == true;
        HostKeyChanged = review?.ReplacesPinnedKey == true;
        HostKeyFingerprint = review is null ? string.Empty : observation.GroupedFingerprint;
        PreviousHostKeyFingerprint = review?.Previous is { } pinned
            ? ServerHostTrustRules.GroupedFingerprint(pinned.Fingerprint)
            : string.Empty;
        PreviousHostKeyConfirmedText = review?.Previous is { } recorded
            ? string.Format(
                T("server_center.pinned_fingerprint_confirmed_at", "Confirmed {0}"),
                recorded.ConfirmedAtUtc.LocalDateTime.ToString("g"))
            : string.Empty;
    }

    /// <summary>丢掉一次待核对的主机密钥，同时解除由它造成的写操作阻断。</summary>
    private void ClearPendingHostKey()
    {
        _pendingHostKey = null;
        NeedsHostKeyConfirmation = false;
        HostKeyReplacesPinnedKey = false;
        HostKeyChanged = false;
        HostKeyFingerprint = string.Empty;
        PreviousHostKeyFingerprint = string.Empty;
        PreviousHostKeyConfirmedText = string.Empty;
    }

    private bool CanProbeHost() => !IsBusy && SelectedHost is not null && !HostKeyChanged;
    private bool CanOpenInstallationWizard() => !IsBusy && SelectedHost is not null && SelectedPlatform is not null && !HostKeyChanged && !HasIncompleteInstallation;
    partial void OnHasIncompleteInstallationChanged(bool value)
    {
        OpenInstallationWizardCommand.NotifyCanExecuteChanged();
        RecoverCommand.NotifyCanExecuteChanged();
    }
    private bool CanMaintain() => CanProbeHost() && SelectedPlatform is not null && HasLastProbe && SelectedHost?.LastVerified?.Installed == true;
    private bool CanRollback() => CanMaintain() && HasPreviousVersion;
    partial void OnHasPreviousVersionChanged(bool value) => RollbackCommand.NotifyCanExecuteChanged();
    private bool CanUninstall() => CanMaintain();
    private bool CanLoadOperationHistory() => !IsBusy && SelectedHost is not null;
    private bool CanRefreshOperation() => !IsBusy && SelectedHost is not null && SelectedPlatform is not null &&
                                          SelectedOperation is not null && !HostKeyChanged;
    private bool CanConfirmHostKey() => !IsBusy && SelectedHost is not null && NeedsHostKeyConfirmation && _pendingHostKey is not null;

    private async Task<ServerCenterSshCredential?> ResolveCredentialAsync(
        ServerHostTarget target, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(SshPassword))
            return new ServerCenterSshCredential.Password(SshPassword);

        var endpoint = ServerCenterSshEndpoint.Create(target.SshHost, target.SshPort, target.SshUserName);
        var saved = await _sshCredentials.FindAsync(endpoint, cancellationToken).ConfigureAwait(true);
        if (saved is not null) return saved.ToCredential();

        ErrorMessage = T("server_center.password_required", "No saved SSH credential exists for this server and user. Add one on the Hosts page or enter a one-time password.");
        return null;
    }

    partial void OnSelectedHostChanged(ServerHostTarget? value)
    {
        // A selector may briefly clear its selection while its current item is replaced.
        // Refreshing the same SSH target must not reset its detected platform or trust state.
        OnPropertyChanged(nameof(HasSelectedHost));
        if (_refreshingHostSelection) return;
        var changedTarget = !string.Equals(_selectedPlatformHostId, value?.HostId, StringComparison.Ordinal);
        _selectedPlatformHostId = value?.HostId;
        ClearPendingHostKey();
        SshPassword = string.Empty;
        if (changedTarget)
        {
            SelectedPlatform = null;
            HasPreviousVersion = false;
            HasIncompleteInstallation = false;
            MaintenanceSudoPassword = string.Empty;
        }
        VerifiedStateText = value?.LastVerified is { } verified ? FormatSnapshot(verified) : string.Empty;
        LastProbeText = string.Empty;
        MaintenanceAddFirewallRule = false;
        DeleteServerData = false;
        RemoveSmb = RemoveNginx = RemoveFrp = RemoveMihomo = false;
        Operations.Clear();
        SelectedOperation = null;
        OnPropertyChanged(nameof(HasOperations));
        OnPropertyChanged(nameof(DeployText));
        OnPropertyChanged(nameof(HasManagedInstallation));
        OnPropertyChanged(nameof(CanManageFirewall));
        OnPropertyChanged(nameof(InstallationDetails));
        UpdateCommand.NotifyCanExecuteChanged();
        RemoveHostCommand.NotifyCanExecuteChanged();
        ConfirmHostKeyCommand.NotifyCanExecuteChanged();
        ProbeHostCommand.NotifyCanExecuteChanged();
        OpenInstallationWizardCommand.NotifyCanExecuteChanged();
        RecoverCommand.NotifyCanExecuteChanged();
        RepairCommand.NotifyCanExecuteChanged();
        RollbackCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        LoadOperationHistoryCommand.NotifyCanExecuteChanged();
        ClearOperationHistoryCommand.NotifyCanExecuteChanged();
        RefreshOperationCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsBusyChanged(bool value)
    {
        if (!value) MaintenanceSudoPassword = string.Empty;
        UpdateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowWorkspaceProgress));
        ClearOperationHistoryCommand.NotifyCanExecuteChanged();
        RemoveHostCommand.NotifyCanExecuteChanged();
        ConfirmHostKeyCommand.NotifyCanExecuteChanged();
        ProbeHostCommand.NotifyCanExecuteChanged();
        OpenInstallationWizardCommand.NotifyCanExecuteChanged();
        RecoverCommand.NotifyCanExecuteChanged();
        RepairCommand.NotifyCanExecuteChanged();
        RollbackCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        LoadOperationHistoryCommand.NotifyCanExecuteChanged();
        RefreshOperationCommand.NotifyCanExecuteChanged();
    }
    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnNeedsHostKeyConfirmationChanged(bool value) => ConfirmHostKeyCommand.NotifyCanExecuteChanged();
    partial void OnHostKeyReplacesPinnedKeyChanged(bool value)
    {
        OnPropertyChanged(nameof(ConfirmHostKeyText));
        OnPropertyChanged(nameof(HostKeyReviewTitle));
        OnPropertyChanged(nameof(HostKeyReviewText));
    }
    partial void OnHostKeyChangedChanged(bool value)
    {
        ProbeHostCommand.NotifyCanExecuteChanged();
        OpenInstallationWizardCommand.NotifyCanExecuteChanged();
        RecoverCommand.NotifyCanExecuteChanged();
        RepairCommand.NotifyCanExecuteChanged();
        RollbackCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        RefreshOperationCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedPlatformChanged(HostPlatformOption? value)
    {
        OnPropertyChanged(nameof(SelectedPlatformText));
        OnPropertyChanged(nameof(ShowMaintenanceSudoPassword));
        ProbeHostCommand.NotifyCanExecuteChanged();
        OpenInstallationWizardCommand.NotifyCanExecuteChanged();
        RecoverCommand.NotifyCanExecuteChanged();
        RepairCommand.NotifyCanExecuteChanged();
        RollbackCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        RefreshOperationCommand.NotifyCanExecuteChanged();
    }
    partial void OnLastProbeTextChanged(string value)
    {
        UpdateCommand.NotifyCanExecuteChanged();
        RepairCommand.NotifyCanExecuteChanged();
        RollbackCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
    }
    partial void OnDeleteServerDataChanged(bool value) {
        if (value) { RemoveSmb = RemoveNginx = RemoveFrp = RemoveMihomo = true; }
        UninstallCommand.NotifyCanExecuteChanged();
    }
    partial void OnRemoveSmbChanged(bool value) { if (!value) DeleteServerData = false; }
    partial void OnRemoveNginxChanged(bool value) { if (!value) DeleteServerData = false; }
    partial void OnRemoveFrpChanged(bool value) { if (!value) DeleteServerData = false; }
    partial void OnRemoveMihomoChanged(bool value) { if (!value) DeleteServerData = false; }
    partial void OnSelectedOperationChanged(ServerCenterOperationRecord? value)
    {
        OperationDiagnostics = string.Empty;
        OnPropertyChanged(nameof(OperationDetailsText));
        RefreshOperationCommand.NotifyCanExecuteChanged();
        if (value is not null && CanRefreshOperation())
        {
            OperationDiagnostics = T("server_center.diagnostics_loading", "Reading deployment log from host…");
            _ = RefreshOperationCommand.ExecuteAsync(null);
        }
    }

    private async Task<ServerDeploymentOperationDto> ExecuteReadOnlyAsync(
        ServerCenterHostSession session,
        ServerCenterDeploymentTools tools,
        ServerDeploymentKind kind,
        ServerDeploymentOptions? options,
        CancellationToken cancellationToken,
        string? sudoPassword = null)
    {
        var request = new ServerDeploymentRequest(ServerDeploymentProtocol.Version, Guid.NewGuid(), kind, options);
        await using var launcher = tools.OpenLauncher();

        var client = new ServerCenterDeploymentClient(session.Transport);
        var staged = await client.StageAsync(
            request, tools.Platform, launcher, null, null, null, null, cancellationToken).ConfigureAwait(true);
        var receipt = await client.ExecuteAsync(staged, cancellationToken, sudoPassword).ConfigureAwait(true);
        await _operationJournal.RecordAsync(ServerCenterOperationRecord.From(session.Target.HostId, receipt), cancellationToken)
            .ConfigureAwait(true);
        return receipt;
    }

    private async Task<ServerDeploymentOperationDto> ExecuteFixedOperationAsync(
        ServerCenterHostSession session,
        ServerCenterDeploymentTools tools,
        ServerDeploymentRequest request,
        CancellationToken cancellationToken,
        string? sudoPassword = null)
    {
        StatusMessage = T("server_center.progress.preparing", "Preparing and uploading installation files…");
        await using var launcher = tools.OpenLauncher();

        var client = new ServerCenterDeploymentClient(session.Transport);
        var staged = await client.StageAsync(
            request, tools.Platform, launcher, null, null, null, null, cancellationToken).ConfigureAwait(true);
        StatusMessage = request.Kind == ServerDeploymentKind.Uninstall
            ? T("server_center.progress.uninstalling", "Uninstalling, please wait…")
            : request.Kind == ServerDeploymentKind.Rollback ? RollbackText + "…"
            : T("server_center.progress.repairing", "Repairing, please wait…");
        var receipt = await client.ExecuteAsync(staged, cancellationToken, sudoPassword).ConfigureAwait(true);
        await _operationJournal.RecordAsync(ServerCenterOperationRecord.From(session.Target.HostId, receipt), cancellationToken)
            .ConfigureAwait(true);
        return receipt;
    }

    private async Task ApplySnapshotAsync(
        ServerHostTarget target,
        ServerHostSnapshotDto snapshot,
        CancellationToken cancellationToken)
    {
        _installationSnapshots[target.HostId] = snapshot;
        var verified = ServerHostTargetRules.ApplyVerifiedState(
            target, ServerHostTargetRules.VerifiedStateFrom(snapshot), DateTimeOffset.UtcNow);
        var saved = await _targets.UpsertAsync(verified, cancellationToken).ConfigureAwait(true);
        ReplaceHost(saved);
        SelectedHost = saved;
        VerifiedStateText = FormatSnapshot(saved.LastVerified!);
    }

    private async Task ReloadOperationHistoryAsync(string hostId, CancellationToken cancellationToken)
    {
        var records = await _operationJournal.LoadAsync(hostId, cancellationToken).ConfigureAwait(true);
        Operations.Clear();
        foreach (var item in records) Operations.Add(item);
        OnPropertyChanged(nameof(HasOperations));
    }

    private void ReplaceHost(ServerHostTarget host)
    {
        var incumbent = Hosts.FirstOrDefault(item => item.HostId == host.HostId);
        var refreshSelection = SelectedHost?.HostId == host.HostId;
        _refreshingHostSelection = refreshSelection;
        try
        {
            if (incumbent is not null) Hosts[Hosts.IndexOf(incumbent)] = host;
            else Hosts.Insert(0, host);
            if (refreshSelection) SelectedHost = host;
        }
        finally { _refreshingHostSelection = false; }
        if (refreshSelection) OnSelectedHostChanged(host);
        OnPropertyChanged(nameof(HasHosts));
    }

    private string FormatProbe(ServerHostProbeDto probe)
    {
        if (SelectedHost is { } host) _hostProbes[host.HostId] = probe;
        OnPropertyChanged(nameof(InstallationDetails));
        return string.Format(
            T("server_center.probe_summary", "{0} · {1} · {2}"),
            probe.HostPlatform, probe.Architecture,
            probe.Elevated ? T("server_center.elevated", "elevated") : T("server_center.not_elevated", "not elevated"));
    }

    private string FormatSnapshot(ServerHostVerifiedState state) => string.Format(
        T("server_center.verified_state", "Verified through SSH at {0}: {1}"),
        state.VerifiedAtUtc.LocalDateTime.ToString("g"),
        !state.Installed ? T("server_center.not_installed", "RelaxKonOS is not installed") :
        state.Healthy ? string.Format(T("server_center.healthy_version", "Healthy · {0}"), state.Version ?? "?") :
        string.Format(T("server_center.unhealthy_version", "Installed but unhealthy · {0}"), state.Version ?? "?"));

    private static bool PlatformMatches(HostPlatformKind platform, HostPlatformKind observed) => platform == observed;

    private static async Task<HostPlatformKind?> DetectHostPlatformAsync(
        ServerCenterHostSession session, CancellationToken cancellationToken)
    {
        var linux = await session.Transport.RunAsync("uname -s", cancellationToken).ConfigureAwait(true);
        if (linux.Succeeded && string.Equals(linux.StandardOutput.Trim(), "Linux", StringComparison.OrdinalIgnoreCase))
            return HostPlatformKind.Linux;

        var windows = await session.Transport.RunAsync(
            "powershell.exe -NoProfile -NonInteractive -Command \"[Console]::Out.Write('Windows')\"", cancellationToken).ConfigureAwait(true);
        return windows.Succeeded && string.Equals(windows.StandardOutput.Trim(), "Windows", StringComparison.OrdinalIgnoreCase)
            ? HostPlatformKind.Windows
            : null;
    }

    private static bool HasUsableCertificate(ServerInstallationOptions installation) =>
        installation.CertificateMode != ServerCertificateMode.Custom ||
        (!string.IsNullOrWhiteSpace(installation.CertificatePath) && File.Exists(installation.CertificatePath) &&
         (installation.CertificateFormat != ServerCertificateFormat.Pem ||
          (!string.IsNullOrWhiteSpace(installation.CertificatePrivateKeyPath) && File.Exists(installation.CertificatePrivateKeyPath))));

    private static async Task<string> ConvertPemCertificateAsync(
        ServerInstallationOptions installation, CancellationToken cancellationToken)
    {
        var output = Path.Combine(Path.GetTempPath(), "relaxkonos-certificate-" + Guid.NewGuid().ToString("N") + ".pfx");
        try
        {
            var certificates = new X509Certificate2Collection();
            certificates.ImportFromPemFile(installation.CertificatePath!);
            using var leaf = X509Certificate2.CreateFromPemFile(
                installation.CertificatePath!, installation.CertificatePrivateKeyPath!);
            foreach (var existing in certificates.Cast<X509Certificate2>()
                         .Where(certificate => string.Equals(certificate.Thumbprint, leaf.Thumbprint, StringComparison.OrdinalIgnoreCase))
                         .ToArray())
                certificates.Remove(existing);
            certificates.Add(leaf);
            var bytes = certificates.Export(X509ContentType.Pkcs12, installation.CertificatePassword);
            await File.WriteAllBytesAsync(output, bytes, cancellationToken).ConfigureAwait(true);
            return output;
        }
        catch
        {
            try { File.Delete(output); }
            catch (IOException) { }
            throw;
        }
    }

    private static ServerInstallMode? RecommendedMode(HostPlatformKind platform, ServerHostProbeDto probe) =>
        platform switch
        {
            HostPlatformKind.Windows when probe.Elevated => ServerInstallMode.WindowsSystem,
            HostPlatformKind.Windows => null,
            // Automatic mode stays unprivileged for non-root accounts; explicit System Mode can authenticate sudo.
            HostPlatformKind.Linux when probe.Elevated => ServerInstallMode.LinuxSystem,
            HostPlatformKind.Linux => ServerInstallMode.LinuxUser,
            _ => null
        };

    private static bool CanUseInstallationMode(
        HostPlatformKind platform, ServerHostProbeDto probe, ServerInstallMode mode) =>
        (platform, mode) switch
        {
            (HostPlatformKind.Windows, ServerInstallMode.WindowsSystem) => probe.Elevated,
            (HostPlatformKind.Linux, ServerInstallMode.LinuxSystem) => probe.Elevated || probe.SudoAvailable,
            (HostPlatformKind.Linux, ServerInstallMode.LinuxUser) => true,
            _ => false
        };

    private static ServerInstallMode? StatusMode(HostPlatformKind platform, ServerHostProbeDto probe) =>
        probe.ExistingMode ?? RecommendedMode(platform, probe);

    private static ServerDeploymentOptions StatusOptions(ServerInstallMode mode) => new(
        ServerPackageSourceKind.OfficialStable,
        ServerNetworkProfile.Loopback,
        ServerDataRetention.Retain,
        mode);

    internal string Text(string key, string fallback) => _localization.Get(key, fallback);

    private string T(string key, string fallback) => Text(key, fallback);
}

public sealed record HostPlatformOption(HostPlatformKind Platform, string DisplayName);

public sealed record ServerInstallationOptions(
    ServerPackageSourceKind Source,
    ServerInstallMode? Mode,
    string? LocalBundlePath,
    string? RemoteBundlePath,
    ServerFileAccessScope FileAccess,
    ServerNetworkProfile Network,
    ServerCertificateMode CertificateMode,
    ServerCertificateFormat CertificateFormat,
    string? CertificatePath,
    string? CertificatePrivateKeyPath,
    string CertificatePassword,
    string SelfSignedIdentities,
    string SudoPassword = "",
    int ServerPort = 5000,
    string? PackageUri = null, string? PackageDigest = null, string? ReleaseCatalogBaseUri = null,
    string? InstallRoot = null, string? DataRoot = null, string? ConfigRoot = null,
    string? StateRoot = null, string? CacheRoot = null,
    IReadOnlyList<string>? FileRoots = null, ServerFileAccessScope? AdministratorFileAccess = null,
    IReadOnlyList<string>? AdministratorFileRoots = null, ServerFileAccessScope? RootFileAccess = null,
    IReadOnlyList<string>? RootFileRoots = null, bool DockerAccess = false, bool AllowUnsupportedSystem = false, bool AddFirewallRule = false);
