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

namespace RelaxKonOS.Client.ViewModels.Login;

/// <summary>登录窗口视图模型。参考 Windows mstsc 远程桌面连接工具：服务器地址 + 用户名 + 密码 + 连接。
/// 通过 IAuthSession 发起登录，状态/错误反馈到 UI。设备信息自动采集（本机名/平台/客户端版本）。</summary>
public partial class LoginViewModel : ObservableObject
{
#if DEBUG
    private const string DebugPasswordEnvironmentVariable = "password";
    private readonly string? _debugPassword = Environment.GetEnvironmentVariable(DebugPasswordEnvironmentVariable);
#endif

    private readonly IAuthSession _session;
    private readonly LoginLocalizationService _localization;
    private bool _loadingSavedProfiles;

    public LoginViewModel(IAuthSession session, LoginLocalizationService localization)
    {
        _session = session;
        _localization = localization;
        SavedProfiles = new ObservableCollection<SavedLoginProfile>();
#if DEBUG
        // Development-only convenience for local integration testing. This is deliberately
        // compiled out of Release builds, and the value is only kept in the login view model.
        Password = _debugPassword ?? string.Empty;
#endif
        _localization.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    public ObservableCollection<SavedLoginProfile> SavedProfiles { get; }

    // 输入与连接状态变化时，自动通知 ConnectCommand 重新评估 CanExecute。
    // 此前缺少通知，导致填写完账号密码后按钮仍处于禁用状态（无法点击）。
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _serverUrl = "http://localhost:5090";

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
    // Debug 和生产版本都默认启用；用户可在共享设备上取消勾选。
    private bool _rememberServer = true;

    [ObservableProperty]
    private bool _rememberPassword = true;

    [ObservableProperty]
    private SavedLoginProfile? _selectedProfile;

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
    public string RemoteDesktopConnectionText => T("login.title", "Remote Desktop Connection");
    public string DisplayLanguageText => T("login.display_language", "Display language:");
    public string ConnectionInstructions => T("login.connection_instructions", "Enter the name of the remote computer you want to connect to.");
    public string CredentialsInstructions => T("login.credentials_instructions", "The credentials below will be used when connecting.");
    public string ComputerLabel => T("login.computer", "Computer:");
    public string IdentifierLabel => T("login.username", "Identifier:");
    public string PasswordLabel => T("login.password", "Password:");
    public string IdentifierPlaceholder => T("login.username_placeholder", "For example: alice");
    public string PasswordPlaceholder => T("login.password_placeholder", "Enter password");
    public string RememberServerText => T("login.remember_server", "Remember this computer and username");
    public string RememberPasswordText => T("login.remember_password", "Save password securely; selecting this computer next time will sign in automatically");
    public string IdentityNotice => T("login.identity_notice", "You will be prompted to verify the identity of the remote computer.");
    public string ConnectionSettingsText => T("login.connection_settings", "Connection settings");
    public string ConnectionSettingsDescription => T("login.connection_settings_description", "RelaxKonOS will open the workspace using this computer's name and local display settings.");
    public string ClientNameText => T("login.client_name", "RelaxKonOS Remote Desktop Client");
    public string ConnectText => T("common.connect", "Connect");

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _hasError;

    partial void OnServerUrlChanged(string value) => ClearError();
    partial void OnIdentifierChanged(string value) => ClearError();
    partial void OnPasswordChanged(string value) => ClearError();
    partial void OnSelectedProfileChanged(SavedLoginProfile? value)
    {
        if (value is not null && !_loadingSavedProfiles)
            ApplySelectedProfile(value);
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
        if (!TryGetServerUrl(out var serverUrl))
        {
            ErrorMessage = T("login.error.invalid_server", "The server address is invalid. Enter a complete address, for example: http://host:port.");
            HasError = true;
            StatusMessage = string.Empty;
            return;
        }

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
            await _session.LoginAsync(serverUrl, request, RememberServer, RememberPassword, ct);
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
            ErrorMessage = T("login.error.invalid_server", "The server address is invalid. Enter a complete address, for example: http://host:port.");
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
        => !IsConnecting
           && !string.IsNullOrWhiteSpace(ServerUrl)
           && !string.IsNullOrWhiteSpace(Identifier)
           && !string.IsNullOrWhiteSpace(Password);

    public async Task LoadSavedProfilesAsync(CancellationToken ct = default)
    {
        _loadingSavedProfiles = true;
        try
        {
            var profiles = await _session.GetSavedProfilesAsync(ct);
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
            ShowOptions = !HasSavedPasswordProfiles;

            // The store is ordered by LastUsedAt, so the first entry is the last selected server.
            // Set it explicitly during startup, then populate fields without initiating a connection.
            if (profiles.FirstOrDefault() is { } lastProfile)
            {
                SelectedProfile = lastProfile;
                ApplySelectedProfile(lastProfile);
            }
        }
        finally
        {
            _loadingSavedProfiles = false;
        }
    }

    private void ApplySelectedProfile(SavedLoginProfile profile)
    {
        ServerUrl = profile.ServerUrl;
        Identifier = profile.Username;
#if DEBUG
        // Keep the debug credential authoritative even when a remembered profile has no password.
        Password = _debugPassword ?? profile.Password ?? string.Empty;
#else
        Password = profile.Password ?? string.Empty;
#endif
        RememberServer = true;
        RememberPassword = profile.HasPassword;
    }

    [RelayCommand]
    private void ToggleOptions()
        => ShowOptions = !ShowOptions;

    [RelayCommand]
    private void TogglePasswordVisibility()
        => IsPasswordVisible = !IsPasswordVisible;

    private bool TryGetServerUrl(out string serverUrl)
    {
        serverUrl = ServerUrl.Trim();
        return Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrWhiteSpace(uri.Host);
    }

    /// <summary>运行时探测客户端宿主平台，而非硬编码。PlatformKind 目前仅 Linux/Windows。</summary>
    private static PlatformKind DetectClientPlatform()
        => OperatingSystem.IsWindows() ? PlatformKind.Windows : PlatformKind.Linux;

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
