using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.ViewModels.Login;

public partial class LoginViewModel
{
    private readonly LoginTunnelStore _tunnelStore;
    private readonly IServerCenterConnectionResolver _tunnelConnections;
    private ServerCenterHostSession? _loginTunnelSession;
    private IServerCenterSshTunnel? _loginTunnel;
    private string? _tunnelCertificateEndpoint;
    private (string Endpoint, ServerCenterSshCredential Credential)? _verifiedTunnelCredential;
    public Func<ServerCenterHostKeyRejectedException, string?, Task<bool>>? ConfirmTunnelHostKeyAsync { get; set; }
    public IReadOnlyList<SshLoginTunnelProfile> SavedTunnels => _tunnelStore.Load();
    [ObservableProperty] private SshLoginTunnelProfile? _selectedTunnel;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyPropertyChangedFor(nameof(TunnelOptionsVisible))]
    private bool _useLoginTunnel;
    [ObservableProperty] private string _tunnelHost = "";
    [ObservableProperty] private string _tunnelPort = "22";
    [ObservableProperty] private string _tunnelUserName = "";
    [ObservableProperty] private string _tunnelSecret = "";
    [ObservableProperty] private string _tunnelPassphrase = "";
    [ObservableProperty] private bool _tunnelUsePrivateKey;
    [ObservableProperty] private bool _rememberSshCredential;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeparateTunnelCredentials))]
    private bool _useServerCredentialsForTunnel;
    [ObservableProperty] private string _tunnelCredentialStatus = "";
    private int _credentialLookupVersion;
    public bool SeparateTunnelCredentials => !UseServerCredentialsForTunnel;
    public string ReuseServerCredentialsText => T("login.tunnel.reuse_server", "Use the Server username and password for SSH");
    [ObservableProperty] private bool _showTunnelConfiguration = true;
    public string ConfigureTunnelText => T("login.tunnel.configure", "Configure SSH tunnel");
    public bool TunnelOptionsVisible => UseLoginTunnel && !UseSshLogin;
    public string TunnelToggleText => T("login.tunnel.toggle", "Connect through an SSH tunnel");
    public string TunnelHostText => T("login.tunnel.host", "SSH host");
    public string TunnelPortText => T("login.tunnel.port", "SSH port");
    public string TunnelUserText => T("login.tunnel.user", "SSH username");
    public string TunnelKeyText => T("login.tunnel.key", "Use a private key (paste PEM/OpenSSH text below)");
    public string TunnelSecretText => T("login.tunnel.secret", "SSH password or private key");
    public string TunnelPassphraseText => T("login.tunnel.passphrase", "Private key passphrase (optional)");
    public string RememberSshText => T("login.tunnel.remember", "Save SSH credentials securely");
    public string TunnelHintText => T("login.tunnel.hint", "Server address above is on the SSH host, e.g. http://127.0.0.1:5000. The local port is automatic. SSH and Server credentials are separate.");
    public string TestTunnelText => T("login.tunnel.test", "Test connection");
    public string TunnelVerifyHint => T("login.tunnel.verify_hint", "Verify this fingerprint with the host administrator. A previous fingerprint means the host key has changed.");
    private string TunnelError(Exception error) => error switch
    {
        LoginTunnelException => error.Message,
        Renci.SshNet.Common.SshAuthenticationException => T("login.tunnel.auth_failed", "SSH authentication failed. Check the SSH username, password or private key."),
        ArgumentException or FormatException => T("login.tunnel.invalid", "Enter a valid SSH host, port, username and remote HTTP(S) loopback URL."),
        _ => T("login.tunnel.failed", "SSH tunnel failed. Check SSH credentials and the remote Server address.")
    };

    partial void OnUseLoginTunnelChanged(bool value)
    {
        OnPropertyChanged(nameof(OwnerDeviceAvailable));
        if (value) ShowOwnerDeviceOptions = false;
        else { _verifiedTunnelCredential = null; TunnelSecret = TunnelPassphrase = ""; }
    }

    partial void OnUseServerCredentialsForTunnelChanged(bool value)
    {
        _verifiedTunnelCredential = null;
        TunnelSecret = TunnelPassphrase = "";
        if (value)
        {
            TunnelUsePrivateKey = false;
            ShowOptions = true;
        }
        _ = RefreshTunnelCredentialStatusAsync();
    }
    partial void OnTunnelHostChanged(string value) => _ = RefreshTunnelCredentialStatusAsync();
    partial void OnTunnelPortChanged(string value) => _ = RefreshTunnelCredentialStatusAsync();
    partial void OnTunnelUserNameChanged(string value) => _ = RefreshTunnelCredentialStatusAsync();

    public async Task RefreshTunnelCredentialStatusAsync()
    {
        var version = ++_credentialLookupVersion;
        TunnelCredentialStatus = "";
        var user = UseServerCredentialsForTunnel ? Identifier : TunnelUserName;
        if (!int.TryParse(TunnelPort, out var port) || !ServerHostTargetRules.IsValidEndpoint(TunnelHost, port, user)) return;
        try
        {
            var record = await _sshCredentials.FindAsync(ServerCenterSshEndpoint.Create(TunnelHost, port, user));
            if (version != _credentialLookupVersion) return;
            if (record is not null)
            {
                RememberSshCredential = true;
                TunnelCredentialStatus = T("login.tunnel.saved", "SSH credentials are saved securely. Leave the SSH credential field empty to use them.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            if (version == _credentialLookupVersion)
                TunnelCredentialStatus = T("login.tunnel.load_failed", "Saved SSH credentials could not be read. Enter them again or retry.");
        }
    }

    partial void OnSelectedTunnelChanged(SshLoginTunnelProfile? value)
    {
        if (value is null || IsConnecting) return;
        UseServerCredentialsForTunnel = false;
        UseLoginTunnel = true;
        TunnelHost = value.Host;
        TunnelPort = value.Port.ToString();
        TunnelUserName = value.UserName;
        ServerUrl = value.RemoteUrl;
        TunnelSecret = TunnelPassphrase = "";
        ShowTunnelConfiguration = false;
        _ = RefreshTunnelCredentialStatusAsync();
    }
    [RelayCommand]
    private void ToggleTunnelConfiguration() => ShowTunnelConfiguration = !ShowTunnelConfiguration;

    private async Task ReleaseLoginTunnelAsync()
    {
        var tunnel = _loginTunnel;
        var session = _loginTunnelSession;
        _loginTunnel = null;
        _loginTunnelSession = null;
        if (_tunnelCertificateEndpoint is { } endpoint) _endpointResolver.UnbindTunnelCertificateScope(endpoint);
        _tunnelCertificateEndpoint = null;
        try { if (tunnel is not null) await tunnel.DisposeAsync(); }
        finally { if (session is not null) await session.DisposeAsync(); }
    }

    private async Task<ServerConnectionIdentity> OpenLoginTunnelAsync(CancellationToken ct)
    {
        if (!int.TryParse(TunnelPort, out var port)) throw new ArgumentException(TunnelPortText);
        var profile = SshLoginTunnelProfile.Create(TunnelHost, port,
            UseServerCredentialsForTunnel ? Identifier : TunnelUserName, ServerUrl);
        var target = ServerHostTargetRules.Create(profile.Host, profile.Port, profile.UserName, null, DateTimeOffset.UtcNow);
        var endpoint = ServerCenterSshEndpoint.Create(profile.Host, profile.Port, profile.UserName);
        var resolver = _tunnelConnections;
        var credential = UseServerCredentialsForTunnel
            ? !string.IsNullOrEmpty(Password)
                ? (ServerCenterSshCredential)new ServerCenterSshCredential.Password(Password)
                : throw new LoginTunnelException(T("login.tunnel.server_password_required", "Enter the Server password to use it for SSH."))
            : !string.IsNullOrEmpty(TunnelSecret)
            ? TunnelUsePrivateKey
                ? (ServerCenterSshCredential)new ServerCenterSshCredential.PrivateKey(TunnelSecret, TunnelPassphrase)
                : new ServerCenterSshCredential.Password(TunnelSecret)
            : _verifiedTunnelCredential is { } cached && cached.Endpoint == SshCredentialRecord.CredentialIdentity(profile.Host, profile.Port, profile.UserName)
                ? cached.Credential
            : (await _sshCredentials.FindAsync(endpoint, ct))?.ToCredential()
                ?? throw new LoginTunnelException(T("login.tunnel.credential_required", "Enter SSH credentials or select a host with saved credentials."));
        await ReleaseLoginTunnelAsync();
        StatusMessage = T("login.tunnel.connecting", "Connecting SSH...");
        try
        {
            try
            {
                _loginTunnelSession = await resolver.ConnectAsync(target, credential, await resolver.PrepareHostKeyGuardAsync(target, ct), ct);
            }
            catch (ServerCenterHostKeyRejectedException rejected)
            {
                var previous = ServerHostTrustRules.Find(await _hostKeys.LoadAsync(ct), endpoint.Host, endpoint.Port, rejected.Observation.Algorithm);
                HostKeyReplacesPinnedKey = rejected.Trust == ServerHostKeyTrust.Changed;
                if (ConfirmTunnelHostKeyAsync is null || !await ConfirmTunnelHostKeyAsync(rejected,
                    previous is null ? null : ServerHostTrustRules.GroupedFingerprint(previous.Fingerprint)))
                    throw new OperationCanceledException();
                ct.ThrowIfCancellationRequested();
                await _hostKeys.TrustAsync(endpoint, rejected.Observation, ct);
                _loginTunnelSession = await resolver.ConnectAsync(target, credential, await resolver.PrepareHostKeyGuardAsync(target, ct), ct);
            }
            // Persist after SSH authentication, even if the remote Server probe or login fails.
            ct.ThrowIfCancellationRequested();
            if (RememberSshCredential)
            {
                ++_credentialLookupVersion;
                SshCredentialSaveResult saved;
                try { saved = await _sshCredentials.SaveAsync(SshCredentialRecord.From(endpoint, credential, DateTimeOffset.UtcNow), ct); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                {
                    saved = SshCredentialSaveResult.WriteFailed;
                }
                TunnelCredentialStatus = saved == SshCredentialSaveResult.Saved
                    ? T("login.tunnel.saved", "SSH credentials are saved securely. Leave the SSH credential field empty to use them.")
                    : T("login.tunnel.not_saved", "Connected, but SSH credentials could not be saved securely.");
            }
            StatusMessage = T("login.tunnel.checking", "SSH connected. Checking remote Server...");
            var remote = new Uri(profile.RemoteUrl);
            _loginTunnel = _loginTunnelSession.Transport.OpenLoopbackTunnel(remote.Port, remote.AbsolutePath);
            var identity = profile.Resolve(_loginTunnel.LocalPort);
            _tunnelCertificateEndpoint = identity.EffectiveBaseUrl;
            _endpointResolver.BindTunnelCertificateScope(identity.EffectiveBaseUrl, identity.ServiceId);
            var check = await _endpointResolver.ResolveAsync(identity.EffectiveBaseUrl, ct);
            if (check.CertificateIssue is { CanTrust: true } review && ConfirmServerCertificateAsync is { } confirm && await confirm(review))
            {
                ct.ThrowIfCancellationRequested();
                _endpointResolver.TrustCertificate(review);
                check = await _endpointResolver.ResolveAsync(identity.EffectiveBaseUrl, ct);
            }
            if (!check.IsResolved) throw new LoginTunnelException(DescribeResolutionError(check));
            ct.ThrowIfCancellationRequested();
            _verifiedTunnelCredential = (SshCredentialRecord.CredentialIdentity(profile.Host, profile.Port, profile.UserName), credential);
            if (RememberServer) { _tunnelStore.Save(profile); OnPropertyChanged(nameof(SavedTunnels)); }
            return identity;
        }
        catch { await ReleaseLoginTunnelAsync(); throw; }
        finally { TunnelSecret = TunnelPassphrase = ""; }
    }

    [RelayCommand]
    private async Task TestTunnelAsync(CancellationToken ct)
    {
        using var lifetime = LinkWindowCancellation(ct);
        ct = lifetime.Token;
        if (IsConnecting || UseSshLogin || !UseLoginTunnel) return;
        IsConnecting = true;
        ClearError();
        try
        {
            await OpenLoginTunnelAsync(ct);
            StatusMessage = T("login.tunnel.ready", "SSH tunnel and remote Server are reachable.");
        }
        catch (OperationCanceledException) { StatusMessage = ""; }
        catch (Exception e) { ShowTunnelConfiguration = true; HasError = true; StatusMessage = ""; ErrorMessage = TunnelError(e); }
        finally { await ReleaseLoginTunnelAsync(); IsConnecting = false; }
    }
    private sealed class LoginTunnelException(string message) : Exception(message);
}
