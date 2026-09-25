using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.ViewModels.ServerCenter;

/// <summary>
/// Login-independent desktop host inventory.  This owns only device-local host metadata: connecting
/// to SSH and deployment actions remain separate steps so adding a host cannot be mistaken for a
/// successful server installation.
/// </summary>
public partial class ServerCenterViewModel : ObservableObject
{
    private readonly IHostTargetStore _targets;
    private readonly IServerCenterConnectionResolver _connections;
    private readonly ISshHostKeyTrustStore _hostKeys;
    private readonly ISshCredentialStore _sshCredentials;
    private readonly LoginLocalizationService _localization;
    private ServerCenterHostKeyObservation? _pendingHostKey;

    public ServerCenterViewModel(
        IHostTargetStore targets,
        IServerCenterConnectionResolver connections,
        ISshHostKeyTrustStore hostKeys,
        ISshCredentialStore sshCredentials,
        LoginLocalizationService localization)
    {
        _targets = targets;
        _connections = connections;
        _hostKeys = hostKeys;
        _sshCredentials = sshCredentials;
        _localization = localization;
        _localization.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    public ObservableCollection<ServerHostTarget> Hosts { get; } = [];
    public bool HasHosts => Hosts.Count > 0;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private string _port = "22";
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private ServerHostTarget? _selectedHost;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _sshPassword = string.Empty;
    [ObservableProperty] private string _hostKeyFingerprint = string.Empty;
    [ObservableProperty] private bool _needsHostKeyConfirmation;
    [ObservableProperty] private bool _hostKeyChanged;
    [ObservableProperty] private bool _saveSshPassword;

    public string Title => T("server_center.title", "Server centre");
    public string Subtitle => T("server_center.subtitle", "Manage the SSH hosts used to install and maintain RelaxKonOS servers.");
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
    public string VerifySshText => T("server_center.verify_ssh", "Verify SSH connection");
    public string ConfirmHostKeyText => T("server_center.confirm_host_key", "I verified this fingerprint");
    public string HostKeyReviewText => T("server_center.host_key_review", "Verify this SSH host-key fingerprint with the host administrator before trusting it:");
    public string HostKeyChangedText => T("server_center.host_key_changed", "The SSH host key changed. Deployment is blocked until an administrator confirms it.");
    public string SaveSshPasswordText => T("server_center.save_ssh_password", "Save this SSH password securely on this device");

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var loaded = await _targets.LoadAsync(cancellationToken).ConfigureAwait(true);
            Hosts.Clear();
            foreach (var target in loaded.OrderByDescending(target => target.LastUsedAtUtc))
                Hosts.Add(target);
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
            var incumbent = Hosts.FirstOrDefault(item => item.HostId == saved.HostId);
            if (incumbent is not null) Hosts.Remove(incumbent);
            Hosts.Insert(0, saved);
            OnPropertyChanged(nameof(HasHosts));
            SelectedHost = saved;
            Host = string.Empty;
            Port = "22";
            UserName = string.Empty;
            DisplayName = string.Empty;
            StatusMessage = T("server_center.host_added", "Host record saved. Verify its SSH host key before deployment.");
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

    [RelayCommand(CanExecute = nameof(CanVerifySsh))]
    private async Task VerifySshAsync(CancellationToken cancellationToken = default)
    {
        var target = SelectedHost;
        if (target is null || string.IsNullOrEmpty(SshPassword)) return;

        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        _pendingHostKey = null;
        NeedsHostKeyConfirmation = false;
        HostKeyChanged = false;
        HostKeyFingerprint = string.Empty;
        try
        {
            await using var session = await _connections.ConnectAsync(
                target.HostId,
                new ServerCenterSshCredential.Password(SshPassword),
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(true);
            var verifiedMessage = string.Format(
                T("server_center.ssh_verified", "SSH host identity verified: {0}"),
                session.ObservedHostKey?.Fingerprint ?? string.Empty);
            if (SaveSshPassword)
            {
                var endpoint = ServerCenterSshEndpoint.Create(target.SshHost, target.SshPort, target.SshUserName);
                var saved = await _sshCredentials.SaveAsync(
                    SshCredentialRecord.From(endpoint, new ServerCenterSshCredential.Password(SshPassword), DateTimeOffset.UtcNow),
                    cancellationToken).ConfigureAwait(true);
                StatusMessage = saved == SshCredentialSaveResult.Saved
                    ? T("server_center.ssh_verified_saved", "SSH host identity verified and the password was saved securely on this device.")
                    : string.Format(T("server_center.ssh_verified_not_saved", "{0} The password was not saved."), verifiedMessage);
            }
            else
            {
                StatusMessage = verifiedMessage;
            }
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
            ErrorMessage = T("server_center.ssh_failed", "Unable to verify SSH. Check the host, account, password, and network.");
        }
        finally
        {
            // This field is never saved; clear the visible editor after every attempt, including a rejected key.
            SshPassword = string.Empty;
            SaveSshPassword = false;
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
            StatusMessage = T("server_center.host_key_trusted", "Host key saved. Verify SSH again before deployment.");
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

    private bool CanVerifySsh() => !IsBusy && SelectedHost is not null && !string.IsNullOrEmpty(SshPassword);
    private bool CanConfirmHostKey() => !IsBusy && SelectedHost is not null && NeedsHostKeyConfirmation && _pendingHostKey is not null;

    partial void OnSelectedHostChanged(ServerHostTarget? value)
    {
        _pendingHostKey = null;
        NeedsHostKeyConfirmation = false;
        HostKeyChanged = false;
        HostKeyFingerprint = string.Empty;
        SshPassword = string.Empty;
        SaveSshPassword = false;
        RemoveHostCommand.NotifyCanExecuteChanged();
        VerifySshCommand.NotifyCanExecuteChanged();
        ConfirmHostKeyCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsBusyChanged(bool value)
    {
        RemoveHostCommand.NotifyCanExecuteChanged();
        VerifySshCommand.NotifyCanExecuteChanged();
        ConfirmHostKeyCommand.NotifyCanExecuteChanged();
    }
    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnSshPasswordChanged(string value) => VerifySshCommand.NotifyCanExecuteChanged();
    partial void OnNeedsHostKeyConfirmationChanged(bool value) => ConfirmHostKeyCommand.NotifyCanExecuteChanged();

    private string T(string key, string fallback) => _localization.Get(key, fallback);
}
