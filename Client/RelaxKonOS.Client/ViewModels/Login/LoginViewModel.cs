using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Collections.ObjectModel;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.ServerCenter;
using RelaxKonOS.Client.Services.ServerCenter;

namespace RelaxKonOS.Client.ViewModels.Login;

/// <summary>登录窗口视图模型。用户选择 RelaxKonOS Server 或 SSH，再用地址、用户名和密码连接。</summary>
public partial class LoginViewModel : ObservableObject
{
    private CancellationTokenSource _windowLifetime = new();

    public void BeginWindowSession()
    {
        _windowLifetime.Cancel();
        _windowLifetime.Dispose();
        _windowLifetime = new();
        StatusMessage = string.Empty;
    }

    public void CancelWindowOperations()
    {
        _windowLifetime.Cancel();
        ClearPendingHostKey();
        _credentialLookupVersion++;
    }

    private CancellationTokenSource LinkWindowCancellation(CancellationToken token)
        => CancellationTokenSource.CreateLinkedTokenSource(token, _windowLifetime.Token);
#if DEBUG
    private const string DebugPasswordEnvironmentVariable = "password";
    private readonly string? _debugPassword = Environment.GetEnvironmentVariable(DebugPasswordEnvironmentVariable);
#endif

    public Func<ServerCertificateReview, Task<bool>>? ConfirmServerCertificateAsync { get; set; }
    private string? _certificateTrustError;
    public string CertificateDialogTitle => T("login.certificate.title", "Verify server TLS certificate");
    public string TrustCertificateText => T("login.certificate.trust", "Trust this server certificate");
    public string CertificateFingerprintLabel => T("login.certificate.fingerprint", "Certificate SHA-256 fingerprint");
    public string PreviousCertificateFingerprintLabel => T("login.certificate.previous", "Previously trusted certificate");
    public string CertificateReviewText(ServerCertificateReview review) => string.Format(
        T("login.certificate.details", "Server: {0}\nSubject: {1}\nIssuer: {2}\nValid from: {3}\nValid until: {4}\n\nThe certificate is not trusted by the system or has changed. Verify its fingerprint before trusting it. Trust is saved only for this server address and certificate in RelaxKonOS."),
        review.Origin, review.Subject, review.Issuer, review.NotBefore.ToString("g"), review.NotAfter.ToString("g"));

    private string DescribeResolutionError(ServerEndpointResolution resolution) => _certificateTrustError ??
        (resolution.CertificateIssue is { } review
            ? review.CanTrust
                ? T("login.error.certificate_untrusted", "The TLS certificate was not trusted. Confirm the server certificate to connect.")
                : T("login.error.certificate_invalid", "The TLS certificate is expired, does not match the server name, or has another validation error. Correct the server certificate.")
            : resolution.IsValidInput
                ? T("login.error.server_unavailable", "Could not find a RelaxKonOS login endpoint at this address. Check the host and port.")
                : T("login.error.invalid_server", "The server address is invalid. Enter a host name or a complete HTTP(S) address, for example: host:port."));

    private readonly IAuthSession _session;
    private readonly LoginLocalizationService _localization;
    private readonly ServerEndpointResolver _endpointResolver;
    private readonly SshDesktopSession _sshDesktop;
    private readonly IHostTargetStore _sshTargets;
    private readonly ISshHostKeyTrustStore _hostKeys;
    private readonly ISshCredentialStore _sshCredentials;
    private ServerCenterHostKeyObservation? _pendingHostKey;
    private ServerHostTarget? _pendingHost;
    private string _relaxServerUrl = "localhost:5090";
    private string _relaxIdentifier = string.Empty;
    private string _sshServerUrl = "localhost:22";
    private string _sshIdentifier = string.Empty;
    private bool _loadingSavedProfiles;
    private bool _loadingSavedSshHosts;

    public LoginViewModel(IAuthSession session, LoginLocalizationService localization, ServerEndpointResolver endpointResolver,
        SshDesktopSession sshDesktop, IHostTargetStore sshTargets, ISshHostKeyTrustStore hostKeys,
        ISshCredentialStore sshCredentials, IServerCenterConnectionResolver tunnelConnections, LoginTunnelStore tunnelStore)
    {
        _session = session;
        _localization = localization;
        _endpointResolver = endpointResolver;
        _sshDesktop = sshDesktop;
        _sshTargets = sshTargets;
        _hostKeys = hostKeys;
        _sshCredentials = sshCredentials;
        _tunnelConnections = tunnelConnections;
        _tunnelStore = tunnelStore;
        _session.StateChanged += (_, _) =>
        {
            if (_session.State == AuthSessionState.Unauthenticated && !IsConnecting)
            {
                _verifiedTunnelCredential = null;
                _ = ReleaseLoginTunnelAsync();
            }
        };
        SavedProfiles = new ObservableCollection<SavedLoginProfile>();
#if DEBUG
        // Development-only convenience for local integration testing. This is deliberately
        // compiled out of Release builds, and the value is only kept in the login view model.
        Password = _debugPassword ?? string.Empty;
#endif
        _localization.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    public ObservableCollection<SavedLoginProfile> SavedProfiles { get; }
    public ObservableCollection<SavedSshLoginProfile> SavedSshHosts { get; } = [];
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(BootstrapWindowsOwnerDeviceCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectOwnerDeviceCommand))]
    [NotifyPropertyChangedFor(nameof(ConnectionInstructions))]
    [NotifyPropertyChangedFor(nameof(ConnectionSettingsDescription))]
    [NotifyPropertyChangedFor(nameof(IdentityNotice))]
    private bool _useSshLogin;

    partial void OnUseSshLoginChanged(bool value)
    {
        OnPropertyChanged(nameof(TunnelOptionsVisible));
        OnPropertyChanged(nameof(OwnerDeviceAvailable));
        OnPropertyChanged(nameof(WindowsOwnerDeviceBootstrapAvailable));
        if (value) ShowOwnerDeviceOptions = false;
        if (value)
        {
            _relaxServerUrl = ServerUrl;
            _relaxIdentifier = Identifier;
            ServerUrl = _sshServerUrl;
            Identifier = _sshIdentifier;
            ShowOptions = true;
            // Settle the picker last: it decides whether the cached pair above or a saved record wins.
            SettleSshHostSelection();
        }
        else
        {
            _sshServerUrl = ServerUrl;
            _sshIdentifier = Identifier;
            ServerUrl = _relaxServerUrl;
            Identifier = _relaxIdentifier;
        }
        Password = string.Empty;
        ClearPendingHostKey();
        ClearError();
        StatusMessage = string.Empty;
    }

    // 输入与连接状态变化时，自动通知 ConnectCommand 重新评估 CanExecute。
    // 此前缺少通知，导致填写完账号密码后按钮仍处于禁用状态（无法点击）。
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(BootstrapWindowsOwnerDeviceCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectOwnerDeviceCommand))]
    private string _serverUrl = "localhost:5090";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _identifier = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _password = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(BootstrapWindowsOwnerDeviceCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectOwnerDeviceCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallOnThisComputerCommand))]
    private bool _isConnecting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(BootstrapWindowsOwnerDeviceCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectOwnerDeviceCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallOnThisComputerCommand))]
    private bool _isDiscoveringServer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectOwnerDeviceCommand))]
    [NotifyCanExecuteChangedFor(nameof(AcceptOwnerDevicePairingCommand))]
    private string _ownerDeviceKeyPassphrase = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OwnerDevicePassphraseInputVisible))]
    private bool _ownerDevicePassphraseRequired;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptOwnerDevicePairingCommand))]
    private string _ownerDevicePairingCode = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OwnerDeviceOptionsToggleText))]
    [NotifyPropertyChangedFor(nameof(CredentialsInstructions))]
    [NotifyPropertyChangedFor(nameof(StandardAuthenticationVisible))]
    [NotifyPropertyChangedFor(nameof(PasswordAuthenticationVisible))]
    private bool _showOwnerDeviceOptions;

    [ObservableProperty]
    // Debug 和生产版本都默认启用；用户可在共享设备上取消勾选。
    private bool _rememberServer = true;

    [ObservableProperty]
    private bool _rememberPassword = true;

    [ObservableProperty]
    private SavedLoginProfile? _selectedProfile;

    [ObservableProperty]
    private SavedSshLoginProfile? _selectedSshHost;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptionsToggleText))]
    [NotifyPropertyChangedFor(nameof(PasswordAuthenticationVisible))]
    private bool _showOptions = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordVisibilityText))]
    private bool _isPasswordVisible;

    [ObservableProperty]
    private bool _hasSavedPasswordProfiles;

    public IReadOnlyList<SystemLanguageOption> Languages => _localization.AvailableLanguages;
    public SystemLanguageOption? SelectedLanguage
    {
        get => Languages.FirstOrDefault(option => string.Equals(option.Culture, _localization.CurrentLanguage, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
                _localization.SetLanguage(value.Culture);
        }
    }

    public string OptionsToggleText => T(ShowOptions ? "login.options.hide" : "login.options.show", ShowOptions ? "Hide options" : "Show options");
    public string PasswordVisibilityText => T(IsPasswordVisible ? "login.password.hide" : "login.password.show", IsPasswordVisible ? "Hide" : "Show");
    public string RemoteDesktopConnectionText => T("login.title", "RelaxKonOS");
    public string LoginModeLabel => T("login.mode", "Login method:");
    public string RelaxLoginText => T("login.mode.relaxkonos", "RelaxKonOS Server");
    public string SshLoginText => T("login.mode.ssh", "SSH");
    public string DisplayLanguageText => T("login.display_language", "Display language:");
    public string ConnectionInstructions => UseSshLogin
        ? T("login.ssh_instructions", "Enter the SSH host name and credentials.")
        : T("login.connection_instructions", "Enter the name of the remote computer you want to connect to.");
    public string CredentialsInstructions => ShowOwnerDeviceOptions && !UseSshLogin
        ? T("login.owner_device.server_only", "For paired-device sign-in, only this Server address is required. Your private key identifies your account.")
        : T("login.credentials_instructions", "The credentials below will be used when connecting.");
    public string ComputerLabel => T("login.computer", "Computer:");
    public string SavedConnectionsText => T("login.saved_connections", "Saved connections");
    public string IdentifierLabel => T("login.username", "Identifier:");
    public string PasswordLabel => T("login.password", "Password:");
    public string IdentifierPlaceholder => T("login.username_placeholder", "For example: alice");
    public string PasswordPlaceholder => T("login.password_placeholder", "Enter password");
    public string RememberServerText => T("login.remember_server", "Remember this computer and username");
    public string RememberPasswordText => T("login.remember_password", "Save password securely; selecting this computer next time will sign in automatically");
    public string IdentityNotice => UseSshLogin
        ? T("login.ssh_identity_notice", "Verify the SSH host key fingerprint before trusting a new host.")
        : T("login.identity_notice", "You will be prompted to verify the identity of the remote computer.");
    public string ConnectionSettingsText => T("login.connection_settings", "Connection settings");
    public string ConnectionSettingsDescription => UseSshLogin
        ? T("login.ssh_connection_description", "SSH opens a desktop with Terminal, Server Centre, SFTP files, Code Editor, and Image Viewer.")
        : T("login.connection_settings_description", "RelaxKonOS will open the workspace using this computer's name and local display settings.");
    public string ClientNameText => T("login.client_name", "RelaxKonOS Remote Desktop Client");
    public string ConnectText => T("common.connect", "Connect");
    public bool OwnerDeviceAvailable => !UseSshLogin && !UseLoginTunnel;
    public bool StandardAuthenticationVisible => !ShowOwnerDeviceOptions;
    public bool PasswordAuthenticationVisible => StandardAuthenticationVisible && ShowOptions;
    public bool WindowsOwnerDeviceBootstrapAvailable => OperatingSystem.IsWindows() && !UseSshLogin;
    public bool OwnerDevicePassphraseInputVisible => OperatingSystem.IsLinux() && OwnerDevicePassphraseRequired;
    public string OwnerDeviceOptionsToggleText => T(ShowOwnerDeviceOptions ? "login.owner_device.options.hide" : "login.owner_device.options.show",
        ShowOwnerDeviceOptions ? "Hide paired-device options" : "Use a paired device key");
    public string OwnerDeviceTitle => T("login.owner_device.title", "Paired device");
    public string OwnerDeviceDescription => T("login.owner_device.description", "Set up this computer once, then sign in with its private key instead of a Server password.");
    public string OwnerDeviceSetupHeading => T("login.owner_device.setup_heading", "First use on this Windows computer");
    public string OwnerDeviceSignInHeading => T("login.owner_device.sign_in_heading", "Already set up on this computer");
    public string OwnerDevicePairingHeading => T("login.owner_device.pairing_heading", "Pair a device from another computer");
    public string BootstrapWindowsOwnerDeviceText => T("login.owner_device.setup", "Set up this Windows device");
    public string ConnectOwnerDeviceText => T("login.owner_device.connect", "Sign in with device key");
    public string OwnerDevicePairingDescription => T("login.owner_device.pairing_description", "New device? Scan a pairing QR code, then paste its code here.");
    public string OwnerDevicePairingCodeText => T("login.owner_device.pairing_code", "Pairing code");
    public string OwnerDeviceKeyPassphraseText => T("login.owner_device.passphrase", "Key-file passphrase");
    public string OwnerDeviceKeyPassphraseHint => T("login.owner_device.passphrase_hint", "Required only when Linux has no desktop keyring.");
    public string AcceptOwnerDevicePairingText => T("login.owner_device.accept", "Pair and sign in");
    // 首次固定与替换已固定的密钥共用同一个对话框，区别只在文案与是否需要并排展示被取代的旧指纹
    // （判定见 SshHostKeyReviewRules）。
    public string ConfirmHostKeyText => HostKeyReplacesPinnedKey
        ? T("login.ssh_replace_host_key", "Accept the new fingerprint; trust and connect")
        : T("login.ssh_confirm_host_key", "I verified this fingerprint; trust and connect");
    public string HostKeyDialogTitle => HostKeyReplacesPinnedKey
        ? T("login.ssh_host_key_changed_title", "SSH host key changed")
        : T("login.ssh_host_key_title", "Verify SSH host key");
    public string PinnedFingerprintLabel => T("login.ssh_pinned_fingerprint", "Fingerprint saved on this device");
    public string ObservedFingerprintLabel => T("login.ssh_observed_fingerprint", "Fingerprint this handshake presented");
    public string CancelText => T("common.cancel", "Cancel");

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _needsHostKeyConfirmation;
    [ObservableProperty] private string _hostKeyFingerprint = string.Empty;
    [ObservableProperty] private string _hostKeyMessage = string.Empty;

    /// <summary>本次核对是「替换已固定的密钥」，而不是「首次固定」。</summary>
    [ObservableProperty] private bool _hostKeyReplacesPinnedKey;
    [ObservableProperty] private string _previousHostKeyFingerprint = string.Empty;
    [ObservableProperty] private string _previousHostKeyConfirmedText = string.Empty;

    partial void OnServerUrlChanged(string value)
    {
        if (!UseSshLogin && SelectedProfile is { ServiceIdKind: ServerServiceIdKind.DirectUrl } profile
            && !SameDirectEndpoint(value, profile.DirectServerUrl))
            DetachSelectedProfileCredential();
        ClearPendingHostKey();
        ClearError();
        if (!IsDiscoveringServer) StatusMessage = string.Empty;
    }
    partial void OnIdentifierChanged(string value)
    {
        if (!UseSshLogin && SelectedProfile is { } profile
            && !string.Equals(value.Trim(), profile.Identifier, StringComparison.Ordinal))
            DetachSelectedProfileCredential();
        ClearPendingHostKey();
        ClearError();
        if (UseServerCredentialsForTunnel) _ = RefreshTunnelCredentialStatusAsync();
    }
    partial void OnPasswordChanged(string value) => ClearError();

    private void DetachSelectedProfileCredential()
    {
        SelectedProfile = null;
        Password = string.Empty;
        RememberPassword = false;
        ShowOptions = true;
    }

    private static bool SameDirectEndpoint(string entered, string? saved)
        => Uri.TryCreate(entered, UriKind.Absolute, out var current) && Uri.TryCreate(saved, UriKind.Absolute, out var previous)
            && current.Scheme == previous.Scheme && string.Equals(current.IdnHost, previous.IdnHost, StringComparison.OrdinalIgnoreCase)
            && current.Port == previous.Port && current.AbsolutePath == previous.AbsolutePath
            && current.UserInfo.Length == 0 && current.Query.Length == 0 && current.Fragment.Length == 0;
    partial void OnRememberServerChanged(bool value)
    {
        if (!value) RememberPassword = false;
    }
    partial void OnSelectedProfileChanged(SavedLoginProfile? value)
    {
        if (value is not null && !_loadingSavedProfiles)
            ApplySelectedProfile(value);
    }
    partial void OnSelectedSshHostChanged(SavedSshLoginProfile? value)
    {
        if (value is not null && !_loadingSavedSshHosts)
            _ = ApplySelectedSshHostAsync(value);
    }

    private void ClearError()
    {
        ErrorMessage = string.Empty;
        HasError = false;
    }

    /// <summary>Shows the actionable reason when a running desktop session can no longer be refreshed.</summary>
    public void ShowSessionExpiredMessage()
    {
        ErrorMessage = T("login.error.session_expired", "Your session expired. Sign in again to continue.");
        HasError = true;
        StatusMessage = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync(CancellationToken ct)
    {
        using var lifetime = LinkWindowCancellation(ct);
        ct = lifetime.Token;
        if (!CanConnect()) return;
        // Normalize once at submission so authentication and saved profiles use the same values.
        Identifier = Identifier.Trim();
        Password = Password.Trim();
        if (UseSshLogin)
        {
            await ConnectSshAsync(ct);
            return;
        }
        IsConnecting = true;
        StatusMessage = T("login.status.connecting", "Connecting...");
        ClearError();

        try
        {
            ServerConnectionIdentity? identity = null;
            if (!UseLoginTunnel)
            {
                var resolution = await ResolveServerEndpointAsync(ct);
                if (!resolution.IsResolved)
                {
                    ErrorMessage = DescribeResolutionError(resolution);
                    HasError = true;
                    StatusMessage = string.Empty;
                    return;
                }
                identity = ServerConnectionIdentityRules.Direct(resolution.Endpoint!);
            }
            if (UseLoginTunnel) identity = await OpenLoginTunnelAsync(ct);
            var request = new LoginRequest(
                Identifier, Password,
                ClientPlatform: DetectClientPlatform(),
                DeviceName: Environment.MachineName,
                ClientVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");
            // The stable identity comes from the selected transport; a tunnel's local port is never saved.
            await _session.LoginAsync(
                identity!, request, RememberServer, RememberPassword, ct);
            _verifiedTunnelCredential = null;
            StatusMessage = T("login.status.opening_desktop", "Connected. Opening desktop...");
        }
        catch (RelaxKonOSAuthException ex)
        {
            ErrorMessage = MapProblemToMessage(ex);
            HasError = true;
            StatusMessage = string.Empty;
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = MapHttpError(ex);
            HasError = true;
            StatusMessage = string.Empty;
        }
        catch (UriFormatException)
        {
            ErrorMessage = T("login.error.invalid_server", "The server address is invalid. Enter a host name or a complete HTTP(S) address, for example: host:port.");
            HasError = true;
            StatusMessage = string.Empty;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = string.Empty;
        }
        catch (Exception ex) when (UseLoginTunnel)
        {
            ShowTunnelConfiguration = true;
            HasError = true;
            ErrorMessage = TunnelError(ex);
            StatusMessage = string.Empty;
        }
        finally
        {
            if (_session.State != AuthSessionState.Authenticated) await ReleaseLoginTunnelAsync();
            IsConnecting = false;
        }
    }

    private bool CanConnect()
        => !IsConnecting && !IsDiscoveringServer
           && !string.IsNullOrWhiteSpace(ServerUrl)
           && !string.IsNullOrWhiteSpace(Identifier)
           && (UseSshLogin || !string.IsNullOrWhiteSpace(Password));

    private bool CanBootstrapWindowsOwnerDevice()
        => OperatingSystem.IsWindows() && !UseSshLogin && !IsConnecting && !IsDiscoveringServer && !string.IsNullOrWhiteSpace(ServerUrl);

    private bool CanConnectOwnerDevice()
        => !UseSshLogin && !IsConnecting && !IsDiscoveringServer && !string.IsNullOrWhiteSpace(ServerUrl);

    private bool CanAcceptOwnerDevicePairing()
        => !UseSshLogin && !IsConnecting && !string.IsNullOrWhiteSpace(OwnerDevicePairingCode);

    [RelayCommand(CanExecute = nameof(CanBootstrapWindowsOwnerDevice))]
    private async Task BootstrapWindowsOwnerDeviceAsync(CancellationToken ct)
    {
        using var lifetime = LinkWindowCancellation(ct);
        ct = lifetime.Token;
        if (!CanBootstrapWindowsOwnerDevice()) return;
        IsConnecting = true;
        ClearError();
        StatusMessage = T("login.owner_device.setting_up", "Generating and registering this device key...");
        try
        {
            var resolution = await ResolveServerEndpointAsync(ct);
            if (!resolution.IsResolved)
            {
                ErrorMessage = DescribeResolutionError(resolution);
                HasError = true;
                StatusMessage = string.Empty;
                return;
            }
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            await _session.BootstrapWindowsOwnerDeviceAsync(ServerConnectionIdentityRules.Direct(resolution.Endpoint!),
                Environment.MachineName, version, RememberServer, ct);
            StatusMessage = T("login.status.opening_desktop", "Connected. Opening desktop...");
        }
        catch (RelaxKonOSAuthException ex) { ErrorMessage = MapProblemToMessage(ex); HasError = true; StatusMessage = string.Empty; }
        catch (OperationCanceledException) { StatusMessage = string.Empty; }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException) { ErrorMessage = ex.Message; HasError = true; StatusMessage = string.Empty; }
        finally { IsConnecting = false; }
    }

    [RelayCommand(CanExecute = nameof(CanConnectOwnerDevice))]
    private async Task ConnectOwnerDeviceAsync(CancellationToken ct)
    {
        using var lifetime = LinkWindowCancellation(ct);
        ct = lifetime.Token;
        if (!CanConnectOwnerDevice()) return;
        IsConnecting = true;
        ClearError();
        StatusMessage = T("login.owner_device.signing_in", "Signing the device challenge...");
        try
        {
            var resolution = await ResolveServerEndpointAsync(ct);
            if (!resolution.IsResolved)
            {
                ErrorMessage = DescribeResolutionError(resolution);
                HasError = true;
                StatusMessage = string.Empty;
                return;
            }
            await _session.LoginWithOwnerDeviceAsync(ServerConnectionIdentityRules.Direct(resolution.Endpoint!), OwnerDeviceKeyPassphrase,
                RememberServer, ct);
            OwnerDeviceKeyPassphrase = string.Empty;
            StatusMessage = T("login.status.opening_desktop", "Connected. Opening desktop...");
        }
        catch (OwnerDeviceKeyPassphraseRequiredException)
        {
            OwnerDevicePassphraseRequired = true;
            ErrorMessage = T("login.owner_device.passphrase_required", "This Linux client has no secure keyring. Enter a device key passphrase of at least 12 characters.");
            HasError = true;
            StatusMessage = string.Empty;
        }
        catch (OwnerDeviceNotPairedException)
        {
            ErrorMessage = T("login.owner_device.not_paired", "This device has not been paired with the selected Server. Set up or recover this Windows device locally, or use a pairing code.");
            HasError = true;
            StatusMessage = string.Empty;
        }
        catch (RelaxKonOSAuthException ex) { ErrorMessage = MapProblemToMessage(ex); HasError = true; StatusMessage = string.Empty; }
        catch (OperationCanceledException) { StatusMessage = string.Empty; }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or CryptographicException) { ErrorMessage = ex.Message; HasError = true; StatusMessage = string.Empty; }
        finally { IsConnecting = false; }
    }

    [RelayCommand(CanExecute = nameof(CanAcceptOwnerDevicePairing))]
    private async Task AcceptOwnerDevicePairingAsync(CancellationToken ct)
    {
        using var lifetime = LinkWindowCancellation(ct);
        ct = lifetime.Token;
        if (!CanAcceptOwnerDevicePairing()) return;
        IsConnecting = true;
        ClearError();
        StatusMessage = T("login.owner_device.pairing", "Pairing this device...");
        try
        {
            var platform = DetectClientPlatform().ToString().ToLowerInvariant();
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            await _session.AcceptOwnerDevicePairingAsync(OwnerDevicePairingCode, Environment.MachineName, platform, version,
                OwnerDeviceKeyPassphrase, RememberServer, ct);
            OwnerDevicePairingCode = string.Empty;
            OwnerDeviceKeyPassphrase = string.Empty;
            StatusMessage = T("login.status.opening_desktop", "Connected. Opening desktop...");
        }
        catch (OwnerDeviceKeyPassphraseRequiredException)
        {
            OwnerDevicePassphraseRequired = true;
            ErrorMessage = T("login.owner_device.passphrase_required", "This Linux client has no secure keyring. Enter a device key passphrase of at least 12 characters.");
            HasError = true;
            StatusMessage = string.Empty;
        }
        catch (RelaxKonOSAuthException ex) { ErrorMessage = MapProblemToMessage(ex); HasError = true; StatusMessage = string.Empty; }
        catch (OperationCanceledException) { StatusMessage = string.Empty; }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or CryptographicException) { ErrorMessage = ex.Message; HasError = true; StatusMessage = string.Empty; }
        finally { IsConnecting = false; }
    }

    public async Task LoadSavedProfilesAsync(CancellationToken ct = default)
    {
        _loadingSavedProfiles = true;
        _loadingSavedSshHosts = true;
        try
        {
            var profiles = await _session.GetSavedProfilesAsync(ct);
            var sshHosts = await _sshTargets.LoadAsync(ct);
            var sshCredentials = await _sshCredentials.LoadAsync(ct);
            SavedProfiles.Clear();
            // Keep an empty, normal-height item in the editable server picker when there is no history.
            // Without it Avalonia renders the drop-down as a nearly invisible separator.
            if (profiles.Count == 0)
            {
                SavedProfiles.Add(new SavedLoginProfile(string.Empty, string.Empty, null, DateTimeOffset.MinValue));
            }
            else
            {
                foreach (var profile in profiles)
                    SavedProfiles.Add(profile);
            }
            HasSavedPasswordProfiles = profiles.Any(profile => profile.HasPassword);
            if (!UseSshLogin) ShowOptions = !HasSavedPasswordProfiles;

            // The store is ordered by LastUsedAt, so the first entry is the last selected server.
            // Set it explicitly during startup, then populate fields without initiating a connection.
            if (!UseSshLogin && profiles.FirstOrDefault() is { } lastProfile)
            {
                SelectedProfile = lastProfile;
                ApplySelectedProfile(lastProfile);
            }

            SavedSshHosts.Clear();
            // Host targets keep deployment metadata, while SSH credentials are keyed by host,
            // port and user.  A login picker must merge both sources so a securely saved password
            // always carries its matching user name instead of borrowing a host's last manager.
            var savedSshLogins = sshHosts
                .Select(host => new SavedSshLoginProfile(host.SshHost, host.SshPort, host.SshUserName, host.LastUsedAtUtc))
                .Concat(sshCredentials.Select(credential => new SavedSshLoginProfile(
                    credential.Host, credential.Port, credential.UserName, credential.SavedAtUtc)))
                .GroupBy(profile => profile.IdentityKey, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(profile => profile.LastUsedAtUtc).First())
                .OrderByDescending(profile => profile.LastUsedAtUtc);
            foreach (var profile in savedSshLogins)
                SavedSshHosts.Add(profile);
            SelectedSshHost = null;
        }
        finally
        {
            _loadingSavedSshHosts = false;
            _loadingSavedProfiles = false;
            // The login window may switch to SSH before its asynchronous local host list finishes
            // loading. Re-evaluate after the list is ready so the selected record fills every field.
            SettleSshHostSelection();
        }
    }

    private void ApplySelectedProfile(SavedLoginProfile profile)
    {
        if (UseSshLogin) return;
        UseLoginTunnel = profile.ServiceIdKind == ServerServiceIdKind.SshTunnelProfile;
        if (UseLoginTunnel)
        {
            var tunnel = SavedTunnels.FirstOrDefault(p => p.ServiceId == profile.ServiceId);
            SelectedTunnel = tunnel;
            if (tunnel is not null) OnSelectedTunnelChanged(tunnel);
            else { ServerUrl = ""; ErrorMessage = T("login.tunnel.missing", "The saved SSH tunnel configuration is missing. Configure it again."); HasError = true; }
        }
        // A managed-tunnel profile has no address to refill until its SSH tunnel is resolved; only a
        // direct profile carries the canonical URL that belongs in this field.
        if (!UseLoginTunnel) ServerUrl = profile.DirectServerUrl ?? string.Empty;
        Identifier = profile.Identifier;
#if DEBUG
        // Keep the debug credential authoritative even when a remembered profile has no password.
        Password = _debugPassword ?? profile.Password ?? string.Empty;
#else
        Password = profile.Password ?? string.Empty;
#endif
        RememberServer = true;
        RememberPassword = profile.HasPassword;
    }

    private async Task ApplySelectedSshHostAsync(SavedSshLoginProfile profile)
    {
        Password = string.Empty;
        ServerUrl = profile.Address;
        Identifier = profile.UserName;
        RememberServer = true;
        var credential = await _sshCredentials.FindAsync(
            ServerCenterSshEndpoint.Create(profile.Host, profile.Port, profile.UserName));
        if (!UseSshLogin || !ReferenceEquals(SelectedSshHost, profile) || !profile.MatchesAddress(ServerUrl)
            || !string.Equals(Identifier.Trim(), profile.UserName, StringComparison.Ordinal)) return;
        Password = credential is { Kind: SshCredentialKind.Password } ? credential.Secret : string.Empty;
        RememberPassword = !string.IsNullOrEmpty(Password);
    }

    /// <summary>
    /// Settles which saved record owns the SSH form. In SSH mode a record owns both the address and
    /// the user name, so this must run <em>after</em> a mode switch has restored the cached inputs:
    /// writing the address makes the editable picker re-select the matching record, and the cached
    /// user name of the earlier SSH session is written after that selection. Re-asserting the record
    /// here keeps the selected record's user name from being overwritten by that stale cache, and
    /// also covers the window where the local record list finishes loading after the SSH switch.
    /// </summary>
    private void SettleSshHostSelection()
    {
        if (!UseSshLogin) return;
        var address = ServerUrl.Trim();
        var selected = SavedSshHosts.FirstOrDefault(profile => profile.MatchesAddress(address))
            // Nothing named in the field yet: the most recent record is the one-click default.
            ?? (string.IsNullOrWhiteSpace(Identifier) ? SavedSshHosts.FirstOrDefault() : null);
        if (selected is null) return;
        if (ReferenceEquals(SelectedSshHost, selected))
            _ = ApplySelectedSshHostAsync(selected);
        else
            SelectedSshHost = selected;
    }

    [RelayCommand]
    private void ToggleOptions()
        => ShowOptions = !ShowOptions;

    [RelayCommand]
    private void ToggleOwnerDeviceOptions()
    {
        ShowOwnerDeviceOptions = !ShowOwnerDeviceOptions;
        if (!ShowOwnerDeviceOptions)
        {
            OwnerDevicePassphraseRequired = false;
            OwnerDeviceKeyPassphrase = string.Empty;
        }
    }

    [RelayCommand]
    private void TogglePasswordVisibility()
        => IsPasswordVisible = !IsPasswordVisible;

    /// <summary>Invoked by the address control when focus leaves it, before credentials are sent.</summary>
    public async Task DiscoverServerEndpointAsync(CancellationToken ct = default)
    {
        using var lifetime = LinkWindowCancellation(ct);
        ct = lifetime.Token;
        if (UseSshLogin || UseLoginTunnel || IsDiscoveringServer || IsConnecting) return;
        if (string.IsNullOrWhiteSpace(ServerUrl)) return;

        var enteredValue = ServerUrl;
        ServerEndpointResolution resolution;
        try { resolution = await ResolveServerEndpointAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        if (UseSshLogin || UseLoginTunnel) return;
        if (resolution.IsResolved || !string.Equals(ServerUrl, enteredValue, StringComparison.Ordinal)) return;

        ErrorMessage = DescribeResolutionError(resolution);
        HasError = true;
    }

    private void ClearPendingHostKey()
    {
        _pendingHostKey = null;
        _pendingHost = null;
        NeedsHostKeyConfirmation = false;
        HostKeyReplacesPinnedKey = false;
        HostKeyFingerprint = string.Empty;
        PreviousHostKeyFingerprint = string.Empty;
        PreviousHostKeyConfirmedText = string.Empty;
        HostKeyMessage = string.Empty;
    }

    partial void OnHostKeyReplacesPinnedKeyChanged(bool value)
    {
        OnPropertyChanged(nameof(ConfirmHostKeyText));
        OnPropertyChanged(nameof(HostKeyDialogTitle));
    }

    /// <summary>Cancels a pending host-key decision without changing the trusted-host store.</summary>
    public void CancelHostKeyConfirmation()
    {
        ClearPendingHostKey();
        StatusMessage = string.Empty;
    }

    private async Task ConnectSshAsync(CancellationToken ct)
    {
        if (!Uri.TryCreate("ssh://" + ServerUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != "ssh" || uri.Host.Length == 0 || uri.Port is <= 0 or > 65535 ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.UserInfo.Length != 0 || !ServerHostTargetRules.IsValidEndpoint(uri.Host, uri.Port, Identifier))
        {
            ErrorMessage = T("login.ssh_target_changed", "Enter a valid SSH host, port and username.");
            HasError = true;
            return;
        }
        IsConnecting = true;
        ClearError();
        ClearPendingHostKey();
        StatusMessage = T("login.status.connecting", "Connecting...");
        ServerHostTarget? target = null;
        try
        {
            var endpoint = ServerCenterSshEndpoint.Create(uri.Host, uri.Port, Identifier);
            var password = Password;
            if (string.IsNullOrEmpty(password))
            {
                var savedCredential = await _sshCredentials.FindAsync(endpoint, ct);
                password = savedCredential is { Kind: SshCredentialKind.Password } ? savedCredential.Secret : string.Empty;
                if (string.IsNullOrEmpty(password))
                {
                    ErrorMessage = T("login.ssh_password_required", "Enter the SSH password or use the saved password for this host.");
                    HasError = true;
                    StatusMessage = string.Empty;
                    return;
                }
            }

            target = ServerHostTargetRules.Create(uri.Host, uri.Port, Identifier, null, DateTimeOffset.UtcNow);
            await _sshDesktop.ConnectAsync(target, password, ct);
            if (RememberServer)
            {
                var existing = await _sshTargets.FindAsync(uri.Host, uri.Port, Identifier, ct);
                target = existing is null
                    ? await _sshTargets.UpsertAsync(target, ct)
                    : existing;
            }
            if (RememberServer && RememberPassword)
            {
                var saved = await _sshCredentials.SaveAsync(
                    SshCredentialRecord.From(endpoint, new ServerCenterSshCredential.Password(password), DateTimeOffset.UtcNow), ct);
                if (saved != SshCredentialSaveResult.Saved)
                    StatusMessage = T("login.ssh_password_not_saved", "Connected, but the SSH password could not be saved securely.");
            }
            Password = string.Empty;
            if (string.IsNullOrEmpty(StatusMessage))
                StatusMessage = T("login.status.opening_desktop", "Connected. Opening desktop...");
        }
        catch (ServerCenterHostKeyRejectedException rejected)
        {
            _pendingHost = target;
            _pendingHostKey = rejected.Observation;
            // 密钥变更时必须把被取代的那条固定记录一并取出交给用户：只看新指纹无法分辨
            // 「重装/重建过的同一台机器」和「这个地址被另一台机器接管」（SshHostKeyReviewRules）。
            ServerHostKeyRecord? previous = null;
            if (rejected.Trust == ServerHostKeyTrust.Changed)
            {
                var known = await _hostKeys.LoadAsync(ct).ConfigureAwait(true);
                previous = ServerHostTrustRules.Find(
                    known, rejected.Observation.Host, rejected.Observation.Port, rejected.Observation.Algorithm);
            }
            var review = SshHostKeyReviewRules.Plan(rejected.Trust, rejected.Observation, previous);
            HostKeyReplacesPinnedKey = review?.ReplacesPinnedKey == true;
            HostKeyFingerprint = review is null ? string.Empty : rejected.Observation.GroupedFingerprint;
            PreviousHostKeyFingerprint = review?.Previous is { } pinned
                ? ServerHostTrustRules.GroupedFingerprint(pinned.Fingerprint)
                : string.Empty;
            PreviousHostKeyConfirmedText = review?.Previous is { } recorded
                ? string.Format(
                    T("login.ssh_pinned_fingerprint_confirmed_at", "Confirmed {0}"),
                    recorded.ConfirmedAtUtc.LocalDateTime.ToString("g"))
                : string.Empty;
            HostKeyMessage = HostKeyReplacesPinnedKey
                ? T("login.ssh_host_key_changed", "The SSH host key changed. Confirm the new fingerprint with the host administrator before trusting it.")
                : T("login.ssh_host_key_unknown", "New SSH host key. Verify this fingerprint with the host administrator before trusting it.");
            NeedsHostKeyConfirmation = review is not null;
            StatusMessage = string.Empty;
        }
        catch (OperationCanceledException) { StatusMessage = string.Empty; }
        catch (Exception)
        {
            ErrorMessage = T("login.ssh_failed", "SSH connection failed. Check the host, username and password.");
            HasError = true;
            StatusMessage = string.Empty;
        }
        finally { IsConnecting = false; }
    }

    [RelayCommand]
    private async Task ConfirmHostKeyAsync(CancellationToken ct)
    {
        using var lifetime = LinkWindowCancellation(ct);
        ct = lifetime.Token;
        var host = _pendingHost;
        var observation = _pendingHostKey;
        if (!UseSshLogin || IsConnecting || !NeedsHostKeyConfirmation || host is null || observation is null)
            return;
        if (!Uri.TryCreate("ssh://" + ServerUrl.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Host, host.SshHost, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != host.SshPort || !string.Equals(Identifier.Trim(), host.SshUserName, StringComparison.Ordinal))
        {
            ClearPendingHostKey();
            return;
        }
        IsConnecting = true;
        try
        {
            await _hostKeys.TrustAsync(ServerCenterSshEndpoint.Create(host.SshHost, host.SshPort, host.SshUserName), observation, ct);
            ClearPendingHostKey();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception)
        {
            ErrorMessage = T("login.ssh_host_key_save_failed", "Unable to save the SSH host key.");
            HasError = true;
            return;
        }
        finally { IsConnecting = false; }
        await ConnectSshAsync(ct);
    }

    /// <summary>Confirms the host key after the modal login prompt has been accepted.</summary>
    public Task ConfirmHostKeyFromDialogAsync() => ConfirmHostKeyAsync(CancellationToken.None);

    private async Task<ServerEndpointResolution> ResolveServerEndpointAsync(CancellationToken ct)
    {
        var enteredValue = ServerUrl;
        _certificateTrustError = null;
        IsDiscoveringServer = true;
        StatusMessage = T("login.status.discovering_server", "Checking secure and standard server endpoints...");
        ClearError();
        try
        {
            var resolution = await _endpointResolver.ResolveAsync(enteredValue, ct);
            ct.ThrowIfCancellationRequested();
            if (resolution.CertificateIssue is { CanTrust: true } review && ConfirmServerCertificateAsync is { } confirm &&
                !UseSshLogin && !UseLoginTunnel && string.Equals(ServerUrl, enteredValue, StringComparison.Ordinal))
            {
                var accepted = await confirm(review);
                ct.ThrowIfCancellationRequested();
                if (accepted && !ct.IsCancellationRequested && !UseSshLogin && !UseLoginTunnel && string.Equals(ServerUrl, enteredValue, StringComparison.Ordinal))
                {
                    try
                    {
                        _endpointResolver.TrustCertificate(review);
                        resolution = await _endpointResolver.ResolveAsync(enteredValue, ct);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        _certificateTrustError = T("login.error.certificate_save_failed", "Could not save certificate trust. Retry the connection.");
                    }
                }
            }
            if (UseSshLogin || UseLoginTunnel) return resolution;
            // A later edit wins over this asynchronous result.
            if (string.Equals(ServerUrl, enteredValue, StringComparison.Ordinal) && resolution.Endpoint is { } endpoint)
            {
                ServerUrl = endpoint;
                StatusMessage = string.Format(
                    T("login.status.server_found", "Server found. Using {0}."), endpoint);
            }
            else if (string.Equals(ServerUrl, enteredValue, StringComparison.Ordinal) && !resolution.IsResolved)
            {
                StatusMessage = string.Empty;
            }
            return resolution;
        }
        finally
        {
            IsDiscoveringServer = false;
        }
    }

    /// <summary>运行时探测客户端平台；它与 Server 宿主平台是不同的 Protocol 语义。</summary>
    private static ClientPlatformKind DetectClientPlatform()
        => OperatingSystem.IsWindows() ? ClientPlatformKind.Windows : ClientPlatformKind.Linux;

    /// <summary>HttpRequestException → 可操作的 UI 文案。重点区分连接拒绝/重置/超时，
    /// 这些通常对应服务器未启动、地址端口不对，或 HTTP/HTTPS 协议不匹配（最易踩坑）。</summary>
    private string MapHttpError(HttpRequestException ex)
    {
        if (Walk<SocketException>(ex) is { } sock)
        {
            return sock.SocketErrorCode switch
            {
                SocketError.ConnectionRefused =>
                    T("login.error.connection_refused", "Unable to connect to the server: the connection was refused. Confirm that the server is running and that the address and port are correct (the development default is http://localhost:5090)."),
                SocketError.ConnectionReset =>
                    T("login.error.connection_reset", "Unable to connect to the server: the remote host closed the connection. Confirm that the server is running and that the client and server use the same protocol (do not mix HTTP and HTTPS)."),
                SocketError.TimedOut =>
                    T("login.error.timeout", "Timed out while connecting to the server. Check the network or server address."),
                _ => $"{T("login.error.unable_to_connect", "Unable to connect to the server:")} {sock.Message}",
            };
        }
        return $"{T("login.error.unable_to_connect", "Unable to connect to the server:")} {ex.Message}";
    }

    /// <summary>沿 InnerException 链查找首个指定类型的异常（HttpRequestException 常包裹多层）。</summary>
    private static T? Walk<T>(Exception? ex) where T : Exception
    {
        while (ex is not null)
        {
            if (ex is T t) return t;
            ex = ex.InnerException;
        }
        return null;
    }

    /// <summary>ProblemDetails.Type → 本地化 UI 文案。错误码见 RelaxKonOS.Login.md 错误处理矩阵。</summary>
    private string MapProblemToMessage(RelaxKonOSAuthException ex) => ex.Type switch
    {
        "https://relaxkonos.app/problems/invalid-credential"  => T("api.auth.invalid_credential", "The username or password is incorrect."),
        "https://relaxkonos.app/problems/account-locked"      => T("api.auth.account_locked", "This account is locked. Contact an administrator."),
        "https://relaxkonos.app/problems/account-disabled"    => T("api.auth.account_disabled", "This account is disabled."),
        "https://relaxkonos.app/problems/password-expired"    => T("api.auth.password_expired", "This password has expired. Change it on the server first."),
        "https://relaxkonos.app/problems/account-expired"     => T("api.auth.account_expired", "This account has expired."),
        "https://relaxkonos.app/problems/account-restriction" => T("api.auth.account_restriction", "This account is restricted from signing in."),
        "https://relaxkonos.app/problems/invalid-input"       => T("api.auth.invalid_input", "Enter all required information."),
        "https://relaxkonos.app/problems/authentication-unavailable" => T("api.auth.authentication_unavailable", "System account authentication is temporarily unavailable. Check the server authentication configuration and try again."),
        "https://relaxkonos.app/problems/owner-device-local-administrator-required" => T("api.auth.owner_device_local_administrator_required", "The current Windows account must be an Administrator to set up the first paired device."),
        "https://relaxkonos.app/problems/owner-device-windows-session-account-required" => T("api.auth.owner_device_windows_session_account_required", "Use the same Windows account that started this local Server."),
        "https://relaxkonos.app/problems/owner-device-bootstrap-complete" => T("api.auth.owner_device_bootstrap_complete", "A paired owner device is already configured for this account. Sign in with its device key or pair another device."),
        "https://relaxkonos.app/problems/login-rate-limited"   => T("api.auth.login_rate_limited", "Too many sign-in attempts. Wait a few minutes and try again."),
        "https://relaxkonos.app/problems/auth-failed"         => T("api.auth.failed", "Sign-in failed. Try again later."),
        _ => T("api.auth.failed_short", "Sign-in failed."),
    };

    private string T(string key, string englishFallback) => _localization.Get(key, englishFallback);
}

/// <summary>
/// An SSH login picker item deliberately has a compact text representation. Avalonia writes an
/// editable ComboBox selection through <see cref="object.ToString"/>, so exposing the full host
/// target record here would corrupt the address field and expand the login layout.
/// </summary>
public sealed record SavedSshLoginProfile(string Host, int Port, string UserName, DateTimeOffset LastUsedAtUtc)
{
    public string IdentityKey => SshCredentialRecord.CredentialIdentity(Host, Port, UserName);
    public string Address => $"{Host}:{Port}";
    public string DisplayText => $"{UserName}@{Address}";

    /// <summary>Whether the address field currently names this record, comparing what <see cref="ToString"/> publishes.</summary>
    public bool MatchesAddress(string address) =>
        !string.IsNullOrWhiteSpace(address) && string.Equals(Address, address.Trim(), StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Address;
}
