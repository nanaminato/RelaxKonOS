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
    [ObservableProperty] private string _sshPassword = string.Empty;
    [ObservableProperty] private string _hostKeyFingerprint = string.Empty;
    [ObservableProperty] private bool _needsHostKeyConfirmation;
    [ObservableProperty] private bool _hostKeyChanged;
    [ObservableProperty] private HostPlatformOption? _selectedPlatform;
    [ObservableProperty] private string _verifiedStateText = string.Empty;
    [ObservableProperty] private string _lastProbeText = string.Empty;
    [ObservableProperty] private bool _deleteServerData;
    [ObservableProperty] private string _uninstallNameConfirmation = string.Empty;
    [ObservableProperty] private ServerCenterOperationRecord? _selectedOperation;

    /// <summary>Workspace-owned modal presentation; the view model owns the deployment action only.</summary>
    public Func<Task>? ShowInstallationWizardAsync { get; set; }

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
    public string ConfirmHostKeyText => T("server_center.confirm_host_key", "I verified this fingerprint");
    public string HostKeyReviewText => T("server_center.host_key_review", "Verify this SSH host-key fingerprint with the host administrator before trusting it:");
    public string HostKeyChangedText => T("server_center.host_key_changed", "The SSH host key changed. Deployment is blocked until an administrator confirms it.");
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
    public string UninstallNameLabel => T("server_center.uninstall_name", "Type the server name to delete data");
    public string OperationHistoryText => T("server_center.operation_history", "Operation history");
    public string LoadOperationHistoryText => T("server_center.load_operation_history", "Load operation history");
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

    private bool CanRemoveHost() => !IsBusy && SelectedHost is not null;

    [RelayCommand(CanExecute = nameof(CanMaintain))]
    private Task RepairAsync(CancellationToken cancellationToken = default) =>
        PerformInstalledOperationAsync(ServerDeploymentKind.Repair, ServerDataRetention.Retain, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanMaintain))]
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
                ErrorMessage = T("server_center.tools_unavailable", "Deployment tools for this platform are unavailable in this client.");
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
            await using var verifier = tools.OpenVerifier();
            var client = new ServerCenterDeploymentClient(session.Transport);
            var staged = await client.StageQueryAsync(record.OperationId, platform.Platform, launcher, verifier, cancellationToken)
                .ConfigureAwait(true);
            var receipt = await client.QueryAsync(staged, cancellationToken).ConfigureAwait(true);
            await _operationJournal.RecordAsync(ServerCenterOperationRecord.From(target.HostId, receipt), cancellationToken)
                .ConfigureAwait(true);

            if (receipt.Snapshot is not null)
                await ApplySnapshotAsync(target, receipt.Snapshot, cancellationToken).ConfigureAwait(true);
            StatusMessage = T("server_center.operation_refreshed", "The selected operation receipt was refreshed from the host.");
            await ReloadOperationHistoryAsync(target.HostId, cancellationToken).ConfigureAwait(true);
        }
        catch (ServerCenterHostKeyRejectedException rejected)
        {
            _pendingHostKey = rejected.Observation;
            HostKeyFingerprint = rejected.Observation.GroupedFingerprint;
            NeedsHostKeyConfirmation = rejected.Trust == ServerHostKeyTrust.Unknown;
            HostKeyChanged = rejected.Trust == ServerHostKeyTrust.Changed;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = string.Empty;
        }
        catch (Exception)
        {
            ErrorMessage = T("server_center.operation_refresh_failed", "The remote operation receipt could not be refreshed. Check SSH access and try again.");
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
        CancellationToken cancellationToken)
    {
        var target = SelectedHost;
        var platform = SelectedPlatform;
        if (target is null || platform is null) return;
        if (retention == ServerDataRetention.Delete &&
            !string.Equals(UninstallNameConfirmation.Trim(), target.DisplayName, StringComparison.Ordinal)) return;

        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        try
        {
            var tools = await _releaseSource.ResolveToolsAsync(platform.Platform, cancellationToken).ConfigureAwait(true);
            if (tools is null)
            {
                ErrorMessage = T("server_center.tools_unavailable", "Deployment tools for this platform are unavailable in this client.");
                return;
            }

            var credential = await ResolveCredentialAsync(target, cancellationToken).ConfigureAwait(true);
            if (credential is null) return;
            await using var session = await _connections.ConnectAsync(
                target.HostId,
                credential,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(true);
            var probeReceipt = await ExecuteReadOnlyAsync(session, tools, ServerDeploymentKind.Probe, null, cancellationToken).ConfigureAwait(true);
            var probe = probeReceipt.Probe;
            if (probe is null || !probe.OsSupported || !PlatformMatches(platform.Platform, probe.HostPlatform) ||
                !probe.ExistingInstalled || probe.ExistingMode is null ||
                !ServerInstallationId.IsValid(probe.ExistingInstallationId))
            {
                ErrorMessage = T("server_center.maintenance_preflight_failed", "The host no longer reports a supported managed installation. This operation is blocked.");
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
                    probe.ExistingMode,
                    null,
                    null,
                    null,
                    null,
                    probe.ExistingInstallationId,
                    null,
                    Confirmed: true));
            var receipt = await ExecuteFixedOperationAsync(session, tools, request, cancellationToken).ConfigureAwait(true);

            // Read the separate status receipt even after uninstall. The install identity is retained
            // locally only as a stable association for preserved data; it is never treated as live API health.
            var status = await ExecuteReadOnlyAsync(
                session, tools, ServerDeploymentKind.Status, StatusOptions(probe.ExistingMode.Value), cancellationToken).ConfigureAwait(true);
            if (status.Snapshot is null)
            {
                ErrorMessage = T("server_center.status_missing", "The deployment finished, but no authoritative SSH-side status receipt was returned.");
                return;
            }

            await ApplySnapshotAsync(target, status.Snapshot, cancellationToken).ConfigureAwait(true);
            LastProbeText = FormatProbe(probe);
            StatusMessage = kind switch
            {
                ServerDeploymentKind.Repair => T("server_center.repair_succeeded", "The current installation was repaired and verified through SSH."),
                ServerDeploymentKind.Rollback => T("server_center.rollback_succeeded", "The previous program version was restored and verified through SSH."),
                ServerDeploymentKind.Uninstall when retention == ServerDataRetention.Retain => T("server_center.uninstall_retained_succeeded", "The server was uninstalled. Managed data was retained on the host."),
                ServerDeploymentKind.Uninstall => T("server_center.uninstall_deleted_succeeded", "The server and managed data were uninstalled."),
                _ => receipt.SafeMessage ?? T("server_center.operation_succeeded", "The server operation completed.")
            };
        }
        catch (ServerCenterHostKeyRejectedException rejected)
        {
            _pendingHostKey = rejected.Observation;
            HostKeyFingerprint = rejected.Observation.GroupedFingerprint;
            NeedsHostKeyConfirmation = rejected.Trust == ServerHostKeyTrust.Unknown;
            HostKeyChanged = rejected.Trust == ServerHostKeyTrust.Changed;
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
            DeleteServerData = false;
            UninstallNameConfirmation = string.Empty;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenInstallationWizard))]
    private Task OpenInstallationWizardAsync() => ShowInstallationWizardAsync?.Invoke() ?? Task.CompletedTask;

    public async Task<bool> DeployAsync(
        ServerInstallationOptions installation, CancellationToken cancellationToken = default)
    {
        var target = SelectedHost;
        var platform = SelectedPlatform;
        if (target is null || platform is null) return false;

        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        string? downloadedRemoteBundle = null;
        string? convertedCertificate = null;
        try
        {
            var tools = await _releaseSource.ResolveToolsAsync(platform.Platform, cancellationToken).ConfigureAwait(true);
            if (tools is null)
            {
                ErrorMessage = T("server_center.tools_unavailable", "Deployment tools for this platform are unavailable in this client.");
                return false;
            }

            var credential = await ResolveCredentialAsync(target, cancellationToken).ConfigureAwait(true);
            if (credential is null) return false;
            await using var session = await _connections.ConnectAsync(
                target.HostId,
                credential,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(true);
            var probeReceipt = await ExecuteReadOnlyAsync(session, tools, ServerDeploymentKind.Probe, null, cancellationToken).ConfigureAwait(true);
            var probe = probeReceipt.Probe;
            if (probe is null || !probe.OsSupported || probe.RuntimeIdentifier is null ||
                !PlatformMatches(platform.Platform, probe.HostPlatform))
            {
                ErrorMessage = T("server_center.unsupported_host", "The selected platform does not match a supported target reported by the host preflight.");
                return false;
            }

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

            if (installation.Source == ServerPackageSourceKind.RemoteBundle)
            {
                if (string.IsNullOrWhiteSpace(installation.RemoteBundlePath) ||
                    !installation.RemoteBundlePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ErrorMessage = T("server_center.remote_bundle_unavailable", "Choose a .zip release bundle from this SSH server.");
                    return false;
                }

                downloadedRemoteBundle = Path.Combine(Path.GetTempPath(),
                    "relaxkonos-server-release-" + Guid.NewGuid().ToString("N") + ".zip");
                await using (var output = new FileStream(downloadedRemoteBundle, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                    await session.Transport.DownloadAsync(installation.RemoteBundlePath, output, cancellationToken).ConfigureAwait(true);
            }

            var release = installation.Source switch
            {
                ServerPackageSourceKind.OfficialStable => await _releaseSource.ResolveReleaseAsync(
                    platform.Platform, probe.RuntimeIdentifier.Value, mode.Value, cancellationToken).ConfigureAwait(true),
                ServerPackageSourceKind.LocalBundle when !string.IsNullOrWhiteSpace(installation.LocalBundlePath) =>
                    await _releaseSource.ResolveLocalBundleAsync(
                        platform.Platform, probe.RuntimeIdentifier.Value, mode.Value, installation.LocalBundlePath, cancellationToken).ConfigureAwait(true),
                ServerPackageSourceKind.RemoteBundle when downloadedRemoteBundle is not null =>
                    await _releaseSource.ResolveLocalBundleAsync(
                        platform.Platform, probe.RuntimeIdentifier.Value, mode.Value, downloadedRemoteBundle, cancellationToken).ConfigureAwait(true),
                _ => null
            };
            if (release is null)
            {
                ErrorMessage = installation.Source is ServerPackageSourceKind.LocalBundle or ServerPackageSourceKind.RemoteBundle
                    ? T("server_center.local_bundle_unavailable", "The selected bundle is not a valid release for this host.")
                    : T("server_center.release_unavailable", "No release is available for this host architecture and installation mode.");
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
                    release.Version,
                    null,
                    release.StagedPackageName,
                    release.PackageDigest,
                    kind == ServerDeploymentKind.Upgrade ? probe.ExistingInstallationId : null,
                    null,
                    installation.FileAccess,
                    installation.CertificateMode,
                    installation.SelfSignedIdentities,
                    Confirmed: true));
            await using var launcher = release.Tools.OpenLauncher();
            await using var verifier = release.Tools.OpenVerifier();
            await using var archive = release.OpenArchive();
            var client = new ServerCenterDeploymentClient(session.Transport);
            await using var certificate = installation.CertificateMode == ServerCertificateMode.Custom
                ? File.OpenRead(convertedCertificate ?? installation.CertificatePath!) : null;
            var staged = await client.StageAsync(
                request, platform.Platform, launcher, verifier, archive, release.Runtime,
                certificate, installation.CertificatePassword, cancellationToken).ConfigureAwait(true);
            var receipt = await client.ExecuteAsync(staged, cancellationToken).ConfigureAwait(true);
            await _operationJournal.RecordAsync(ServerCenterOperationRecord.From(target.HostId, receipt), cancellationToken)
                .ConfigureAwait(true);

            // A successful launcher process is only a transport result.  Read a separate SSH-side
            // status receipt before declaring success or refreshing the local cached state.
            var status = await ExecuteReadOnlyAsync(
                session, tools, ServerDeploymentKind.Status, StatusOptions(mode.Value), cancellationToken).ConfigureAwait(true);
            if (status.Snapshot is null)
            {
                ErrorMessage = T("server_center.status_missing", "The deployment finished, but no authoritative SSH-side status receipt was returned.");
                return false;
            }
            var verified = ServerHostTargetRules.ApplyVerifiedState(
                target, ServerHostTargetRules.VerifiedStateFrom(status.Snapshot), DateTimeOffset.UtcNow);
            var saved = await _targets.UpsertAsync(verified, cancellationToken).ConfigureAwait(true);
            ReplaceHost(saved);
            SelectedHost = saved;
            VerifiedStateText = FormatSnapshot(saved.LastVerified!);
            LastProbeText = FormatProbe(probe);
            StatusMessage = kind == ServerDeploymentKind.Install
                ? T("server_center.install_succeeded", "RelaxKonOS was installed and verified through SSH. Return to the login window to sign in.")
                : T("server_center.update_succeeded", "RelaxKonOS was updated and verified through SSH. Sign in again if the existing session was interrupted.");
            return true;
        }
        catch (ServerCenterHostKeyRejectedException rejected)
        {
            _pendingHostKey = rejected.Observation;
            HostKeyFingerprint = rejected.Observation.GroupedFingerprint;
            NeedsHostKeyConfirmation = rejected.Trust == ServerHostKeyTrust.Unknown;
            HostKeyChanged = rejected.Trust == ServerHostKeyTrust.Changed;
            return false;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = string.Empty;
            return false;
        }
        catch (Exception)
        {
            ErrorMessage = T("server_center.deploy_failed", "The server operation could not be completed. Its SSH-side receipt can be checked from this host later.");
            return false;
        }
        finally
        {
            if (downloadedRemoteBundle is not null)
            {
                try { File.Delete(downloadedRemoteBundle); }
                catch (IOException) { }
            }
            if (convertedCertificate is not null)
            {
                try { File.Delete(convertedCertificate); }
                catch (IOException) { }
            }
            SshPassword = string.Empty;
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
                ErrorMessage = T("server_center.tools_unavailable", "Deployment tools for this platform are unavailable in this client.");
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

            // Status is separate from reachability/preflight.  Query it in the same trusted session
            // so a host with a stopped API is not incorrectly offered a reinstall.
            var probeFacts = probe.Probe;
            var statusMode = probeFacts is null ? null : StatusMode(platform.Platform, probeFacts);
            if (statusMode is null)
            {
                ErrorMessage = T("server_center.status_mode_missing", "The host did not report an installation mode that can be checked safely.");
                return;
            }
            var status = await ExecuteReadOnlyAsync(
                session, tools, ServerDeploymentKind.Status, StatusOptions(statusMode.Value), cancellationToken).ConfigureAwait(true);
            if (status.Snapshot is not null)
            {
                var verified = ServerHostTargetRules.ApplyVerifiedState(
                    target, ServerHostTargetRules.VerifiedStateFrom(status.Snapshot), DateTimeOffset.UtcNow);
                var saved = await _targets.UpsertAsync(verified, cancellationToken).ConfigureAwait(true);
                ReplaceHost(saved);
                SelectedHost = saved;
                VerifiedStateText = FormatSnapshot(saved.LastVerified!);
            }
            if (probe.Probe is not null)
                LastProbeText = FormatProbe(probe.Probe);
            StatusMessage = T("server_center.probe_succeeded", "Host preflight and SSH-side status check completed.");
        }
        catch (ServerCenterHostKeyRejectedException rejected)
        {
            _pendingHostKey = rejected.Observation;
            HostKeyFingerprint = rejected.Observation.GroupedFingerprint;
            NeedsHostKeyConfirmation = rejected.Trust == ServerHostKeyTrust.Unknown;
            HostKeyChanged = rejected.Trust == ServerHostKeyTrust.Changed;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = string.Empty;
        }
        catch (Exception)
        {
            ErrorMessage = T("server_center.probe_failed", "Host preflight could not be completed. Check SSH access and the selected host platform.");
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
            _pendingHostKey = rejected.Observation;
            HostKeyFingerprint = rejected.Observation.GroupedFingerprint;
            NeedsHostKeyConfirmation = rejected.Trust == ServerHostKeyTrust.Unknown;
            HostKeyChanged = rejected.Trust == ServerHostKeyTrust.Changed;
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
            _pendingHostKey = rejected.Observation;
            HostKeyFingerprint = rejected.Observation.GroupedFingerprint;
            NeedsHostKeyConfirmation = rejected.Trust == ServerHostKeyTrust.Unknown;
            HostKeyChanged = rejected.Trust == ServerHostKeyTrust.Changed;
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
            await _hostKeys.TrustAsync(endpoint, observation, cancellationToken).ConfigureAwait(true);
            _pendingHostKey = null;
            NeedsHostKeyConfirmation = false;
            HostKeyFingerprint = string.Empty;
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

    private bool CanProbeHost() => !IsBusy && SelectedHost is not null && !HostKeyChanged;
    private bool CanOpenInstallationWizard() => !IsBusy && SelectedHost is not null && SelectedPlatform is not null && !HostKeyChanged;
    private bool CanMaintain() => CanProbeHost() && SelectedPlatform is not null && HasLastProbe && SelectedHost?.LastVerified?.Installed == true;
    private bool CanUninstall() => CanMaintain() &&
                                   (!DeleteServerData || string.Equals(
                                       UninstallNameConfirmation.Trim(), SelectedHost?.DisplayName, StringComparison.Ordinal));
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
        var changedTarget = !string.Equals(_selectedPlatformHostId, value?.HostId, StringComparison.Ordinal);
        _selectedPlatformHostId = value?.HostId;
        _pendingHostKey = null;
        NeedsHostKeyConfirmation = false;
        HostKeyChanged = false;
        HostKeyFingerprint = string.Empty;
        SshPassword = string.Empty;
        if (changedTarget) SelectedPlatform = null;
        VerifiedStateText = value?.LastVerified is { } verified ? FormatSnapshot(verified) : string.Empty;
        LastProbeText = string.Empty;
        DeleteServerData = false;
        UninstallNameConfirmation = string.Empty;
        Operations.Clear();
        SelectedOperation = null;
        OnPropertyChanged(nameof(HasOperations));
        OnPropertyChanged(nameof(DeployText));
        RemoveHostCommand.NotifyCanExecuteChanged();
        ConfirmHostKeyCommand.NotifyCanExecuteChanged();
        ProbeHostCommand.NotifyCanExecuteChanged();
        OpenInstallationWizardCommand.NotifyCanExecuteChanged();
        RepairCommand.NotifyCanExecuteChanged();
        RollbackCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        LoadOperationHistoryCommand.NotifyCanExecuteChanged();
        RefreshOperationCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsBusyChanged(bool value)
    {
        RemoveHostCommand.NotifyCanExecuteChanged();
        ConfirmHostKeyCommand.NotifyCanExecuteChanged();
        ProbeHostCommand.NotifyCanExecuteChanged();
        OpenInstallationWizardCommand.NotifyCanExecuteChanged();
        RepairCommand.NotifyCanExecuteChanged();
        RollbackCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        LoadOperationHistoryCommand.NotifyCanExecuteChanged();
        RefreshOperationCommand.NotifyCanExecuteChanged();
    }
    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnNeedsHostKeyConfirmationChanged(bool value) => ConfirmHostKeyCommand.NotifyCanExecuteChanged();
    partial void OnHostKeyChangedChanged(bool value)
    {
        ProbeHostCommand.NotifyCanExecuteChanged();
        OpenInstallationWizardCommand.NotifyCanExecuteChanged();
        RepairCommand.NotifyCanExecuteChanged();
        RollbackCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        RefreshOperationCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedPlatformChanged(HostPlatformOption? value)
    {
        OnPropertyChanged(nameof(SelectedPlatformText));
        ProbeHostCommand.NotifyCanExecuteChanged();
        OpenInstallationWizardCommand.NotifyCanExecuteChanged();
        RepairCommand.NotifyCanExecuteChanged();
        RollbackCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        RefreshOperationCommand.NotifyCanExecuteChanged();
    }
    partial void OnLastProbeTextChanged(string value)
    {
        RepairCommand.NotifyCanExecuteChanged();
        RollbackCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
    }
    partial void OnDeleteServerDataChanged(bool value) => UninstallCommand.NotifyCanExecuteChanged();
    partial void OnUninstallNameConfirmationChanged(string value) => UninstallCommand.NotifyCanExecuteChanged();
    partial void OnSelectedOperationChanged(ServerCenterOperationRecord? value) => RefreshOperationCommand.NotifyCanExecuteChanged();

    private async Task<ServerDeploymentOperationDto> ExecuteReadOnlyAsync(
        ServerCenterHostSession session,
        ServerCenterDeploymentTools tools,
        ServerDeploymentKind kind,
        ServerDeploymentOptions? options,
        CancellationToken cancellationToken)
    {
        var request = new ServerDeploymentRequest(ServerDeploymentProtocol.Version, Guid.NewGuid(), kind, options);
        await using var launcher = tools.OpenLauncher();
        await using var verifier = tools.OpenVerifier();
        var client = new ServerCenterDeploymentClient(session.Transport);
        var staged = await client.StageAsync(
            request, tools.Platform, launcher, verifier, null, null, null, null, cancellationToken).ConfigureAwait(true);
        var receipt = await client.ExecuteAsync(staged, cancellationToken).ConfigureAwait(true);
        await _operationJournal.RecordAsync(ServerCenterOperationRecord.From(session.Target.HostId, receipt), cancellationToken)
            .ConfigureAwait(true);
        return receipt;
    }

    private async Task<ServerDeploymentOperationDto> ExecuteFixedOperationAsync(
        ServerCenterHostSession session,
        ServerCenterDeploymentTools tools,
        ServerDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        await using var launcher = tools.OpenLauncher();
        await using var verifier = tools.OpenVerifier();
        var client = new ServerCenterDeploymentClient(session.Transport);
        var staged = await client.StageAsync(
            request, tools.Platform, launcher, verifier, null, null, null, null, cancellationToken).ConfigureAwait(true);
        var receipt = await client.ExecuteAsync(staged, cancellationToken).ConfigureAwait(true);
        await _operationJournal.RecordAsync(ServerCenterOperationRecord.From(session.Target.HostId, receipt), cancellationToken)
            .ConfigureAwait(true);
        return receipt;
    }

    private async Task ApplySnapshotAsync(
        ServerHostTarget target,
        ServerHostSnapshotDto snapshot,
        CancellationToken cancellationToken)
    {
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
        if (incumbent is not null) Hosts.Remove(incumbent);
        Hosts.Insert(0, host);
        OnPropertyChanged(nameof(HasHosts));
    }

    private string FormatProbe(ServerHostProbeDto probe) => string.Format(
        T("server_center.probe_summary", "{0} · {1} · {2}"),
        probe.HostPlatform, probe.Architecture,
        probe.Elevated ? T("server_center.elevated", "elevated") : T("server_center.not_elevated", "not elevated"));

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
            // The fixed Linux launcher deliberately never accepts an interactive sudo password over
            // this channel. Until a separately authenticated sudo elevation flow exists, only an
            // already-root SSH session may select System Mode; otherwise use User Mode.
            HostPlatformKind.Linux when probe.Elevated => ServerInstallMode.LinuxSystem,
            HostPlatformKind.Linux => ServerInstallMode.LinuxUser,
            _ => null
        };

    private static bool CanUseInstallationMode(
        HostPlatformKind platform, ServerHostProbeDto probe, ServerInstallMode mode) =>
        (platform, mode) switch
        {
            (HostPlatformKind.Windows, ServerInstallMode.WindowsSystem) => probe.Elevated,
            (HostPlatformKind.Linux, ServerInstallMode.LinuxSystem) => probe.Elevated,
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
    string SelfSignedIdentities);
