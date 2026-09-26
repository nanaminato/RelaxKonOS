using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
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
#if DEBUG
    private const string DebugPasswordEnvironmentVariable = "password";
    private readonly string? _debugPassword = Environment.GetEnvironmentVariable(DebugPasswordEnvironmentVariable);
#endif

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
        ISshCredentialStore sshCredentials)
    {
        _session = session;
        _localization = localization;
        _endpointResolver = endpointResolver;
        _sshDesktop = sshDesktop;
        _sshTargets = sshTargets;
        _hostKeys = hostKeys;
        _sshCredentials = sshCredentials;
        SavedProfiles = new ObservableCollection<SavedLoginProfile>();
#if DEBUG
        // Development-only convenience for local integration testing. This is deliberately
        // compiled out of Release builds, and the value is only kept in the login view model.
        Password = _debugPassword ?? string.Empty;
#endif
        _localization.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    public ObservableCollection<SavedLoginProfile> SavedProfiles { get; }
    public ObservableCollection<ServerHostTarget> SavedSshHosts { get; } = [];
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyPropertyChangedFor(nameof(ConnectionInstructions))]
    [NotifyPropertyChangedFor(nameof(ConnectionSettingsDescription))]
    [NotifyPropertyChangedFor(nameof(IdentityNotice))]
    private bool _useSshLogin;

    partial void OnUseSshLoginChanged(bool value)
    {
        if (value)
        {
            _relaxServerUrl = ServerUrl;
            _relaxIdentifier = Identifier;
            ServerUrl = _sshServerUrl;
            Identifier = _sshIdentifier;
            ShowOptions = true;
            if (string.IsNullOrWhiteSpace(Identifier) && SavedSshHosts.FirstOrDefault() is { } lastSshHost)
                SelectedSshHost = lastSshHost;
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
    private string _serverUrl = "localhost:5090";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _identifier = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _password = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _isConnecting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _isDiscoveringServer;

    [ObservableProperty]
    // Debug 和生产版本都默认启用；用户可在共享设备上取消勾选。
    private bool _rememberServer = true;

    [ObservableProperty]
    private bool _rememberPassword = true;

    [ObservableProperty]
    private SavedLoginProfile? _selectedProfile;

    [ObservableProperty]
    private ServerHostTarget? _selectedSshHost;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptionsToggleText))]
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
    public string CredentialsInstructions => T("login.credentials_instructions", "The credentials below will be used when connecting.");
    public string ComputerLabel => T("login.computer", "Computer:");
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
        ? T("login.ssh_connection_description", "SSH opens a desktop with Terminal, Server centre and SFTP files.")
        : T("login.connection_settings_description", "RelaxKonOS will open the workspace using this computer's name and local display settings.");
    public string ClientNameText => T("login.client_name", "RelaxKonOS Remote Desktop Client");
    public string ConnectText => T("common.connect", "Connect");
    public string ConfirmHostKeyText => T("login.ssh_confirm_host_key", "I verified this fingerprint; trust and connect");
    public string HostKeyDialogTitle => T("login.ssh_host_key_title", "Verify SSH host key");
    public string CancelText => T("common.cancel", "Cancel");

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _needsHostKeyConfirmation;
    [ObservableProperty] private string _hostKeyFingerprint = string.Empty;
    [ObservableProperty] private string _hostKeyMessage = string.Empty;

    partial void OnServerUrlChanged(string value)
    {
        ClearPendingHostKey();
        ClearError();
        if (!IsDiscoveringServer) StatusMessage = string.Empty;
    }
    partial void OnIdentifierChanged(string value)
    {
        ClearPendingHostKey();
        ClearError();
    }
    partial void OnPasswordChanged(string value) => ClearError();
    partial void OnRememberServerChanged(bool value)
    {
        if (!value) RememberPassword = false;
    }
    partial void OnSelectedProfileChanged(SavedLoginProfile? value)
    {
        if (value is not null && !_loadingSavedProfiles)
            ApplySelectedProfile(value);
    }
    partial void OnSelectedSshHostChanged(ServerHostTarget? value)
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
        if (UseSshLogin)
        {
            await ConnectSshAsync(ct);
            return;
        }
        var resolution = await ResolveServerEndpointAsync(ct);
        if (!resolution.IsResolved)
        {
            ErrorMessage = resolution.IsValidInput
                ? T("login.error.server_unavailable", "Could not find a RelaxKonOS login endpoint at this address. Check the host and port.")
                : T("login.error.invalid_server", "The server address is invalid. Enter a host name or a complete HTTP(S) address, for example: host:port.");
            HasError = true;
            StatusMessage = string.Empty;
            return;
        }

        var serverUrl = resolution.Endpoint!;

        IsConnecting = true;
        StatusMessage = T("login.status.connecting", "Connecting...");
        ClearError();

        try
        {
            var request = new LoginRequest(
                Identifier, Password,
                ClientPlatform: DetectClientPlatform(),
                DeviceName: Environment.MachineName,
                ClientVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");
            // A hand-entered address is always a direct login: its canonical URL is the stable identity,
            // while the resolved endpoint is only this session's transport address.
            await _session.LoginAsync(
                ServerConnectionIdentityRules.Direct(serverUrl), request, RememberServer, RememberPassword, ct);
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
        finally
        {
            IsConnecting = false;
        }
    }

    private bool CanConnect()
        => !IsConnecting && !IsDiscoveringServer
           && !string.IsNullOrWhiteSpace(ServerUrl)
           && !string.IsNullOrWhiteSpace(Identifier)
           && (UseSshLogin || !string.IsNullOrWhiteSpace(Password));

    public async Task LoadSavedProfilesAsync(CancellationToken ct = default)
    {
        _loadingSavedProfiles = true;
        _loadingSavedSshHosts = true;
        try
        {
            var profiles = await _session.GetSavedProfilesAsync(ct);
            var sshHosts = await _sshTargets.LoadAsync(ct);
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
            foreach (var host in sshHosts.OrderByDescending(host => host.LastUsedAtUtc))
                SavedSshHosts.Add(host);
            SelectedSshHost = null;
        }
        finally
        {
            _loadingSavedSshHosts = false;
            _loadingSavedProfiles = false;
        }
    }

    private void ApplySelectedProfile(SavedLoginProfile profile)
    {
        if (UseSshLogin) return;
        // A managed-tunnel profile has no address to refill until its SSH tunnel is resolved; only a
        // direct profile carries the canonical URL that belongs in this field.
        ServerUrl = profile.DirectServerUrl ?? string.Empty;
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

    private async Task ApplySelectedSshHostAsync(ServerHostTarget host)
    {
        Password = string.Empty;
        ServerUrl = $"{host.SshHost}:{host.SshPort}";
        Identifier = host.SshUserName;
        RememberServer = true;
        var credential = await _sshCredentials.FindAsync(
            ServerCenterSshEndpoint.Create(host.SshHost, host.SshPort, host.SshUserName));
        if (!UseSshLogin || !ReferenceEquals(SelectedSshHost, host)) return;
        Password = credential is { Kind: SshCredentialKind.Password } ? credential.Secret : string.Empty;
        RememberPassword = !string.IsNullOrEmpty(Password);
    }

    [RelayCommand]
    private void ToggleOptions()
        => ShowOptions = !ShowOptions;

    [RelayCommand]
    private void TogglePasswordVisibility()
        => IsPasswordVisible = !IsPasswordVisible;

    /// <summary>Invoked by the address control when focus leaves it, before credentials are sent.</summary>
    public async Task DiscoverServerEndpointAsync(CancellationToken ct = default)
    {
        if (UseSshLogin) return;
        if (string.IsNullOrWhiteSpace(ServerUrl)) return;

        var enteredValue = ServerUrl;
        var resolution = await ResolveServerEndpointAsync(ct);
        if (UseSshLogin) return;
        if (resolution.IsResolved || !string.Equals(ServerUrl, enteredValue, StringComparison.Ordinal)) return;

        ErrorMessage = resolution.IsValidInput
            ? T("login.error.server_unavailable", "Could not find a RelaxKonOS login endpoint at this address. Check the host and port.")
            : T("login.error.invalid_server", "The server address is invalid. Enter a host name or a complete HTTP(S) address, for example: host:port.");
        HasError = true;
    }

    private void ClearPendingHostKey()
    {
        _pendingHostKey = null;
        _pendingHost = null;
        NeedsHostKeyConfirmation = false;
        HostKeyFingerprint = string.Empty;
        HostKeyMessage = string.Empty;
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
                var existing = await _sshTargets.FindByEndpointAsync(uri.Host, uri.Port, ct);
                target = existing is null
                    ? await _sshTargets.UpsertAsync(target, ct)
                    : string.Equals(existing.SshUserName, Identifier.Trim(), StringComparison.Ordinal)
                        ? existing
                        : await _sshTargets.UpsertAsync(existing with { SshUserName = Identifier.Trim() }, ct);
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
            HostKeyFingerprint = rejected.Observation.GroupedFingerprint;
            HostKeyMessage = rejected.Trust == ServerHostKeyTrust.Changed
                ? T("login.ssh_host_key_changed", "The SSH host key changed. Confirm the new fingerprint with the host administrator before trusting it.")
                : T("login.ssh_host_key_unknown", "New SSH host key. Verify this fingerprint with the host administrator before trusting it.");
            NeedsHostKeyConfirmation = true;
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
        IsDiscoveringServer = true;
        StatusMessage = T("login.status.discovering_server", "Checking secure and standard server endpoints...");
        ClearError();
        try
        {
            var resolution = await _endpointResolver.ResolveAsync(enteredValue, ct);
            if (UseSshLogin) return resolution;
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
        "https://relaxkonos.app/problems/auth-failed"         => T("api.auth.failed", "Sign-in failed. Try again later."),
        _ => string.IsNullOrEmpty(ex.Detail) ? T("api.auth.failed_short", "Sign-in failed.") : ex.Detail,
    };

    private string T(string key, string englishFallback) => _localization.Get(key, englishFallback);
}
