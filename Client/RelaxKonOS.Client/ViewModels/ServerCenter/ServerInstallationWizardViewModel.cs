using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.ViewModels.ServerCenter;

/// <summary>Short, modal install flow. It collects only supported package and mode choices before touching the host.</summary>
public sealed partial class ServerInstallationWizardViewModel : ObservableObject
{
    private readonly ServerCenterViewModel _serverCenter;
    private readonly Action _close;
    private readonly Func<Task<string?>> _chooseServerBundle;
    private readonly Func<Task> _showHostAddresses;
    private readonly Func<ServerInstallationOptions, Task<bool>> _deploy;
    public bool IsLocalInstallation { get; }
    public bool CanShowHostAddresses => !IsLocalInstallation;

    public ServerInstallationWizardViewModel(ServerCenterViewModel serverCenter, Action close,
        Func<Task<string?>> chooseServerBundle, Func<Task> showHostAddresses,
        Func<ServerInstallationOptions, Task<bool>>? localDeploy = null)
    {
        _serverCenter = serverCenter;
        _close = close;
        _chooseServerBundle = chooseServerBundle;
        _showHostAddresses = showHostAddresses;
        IsLocalInstallation = localDeploy is not null;
        _deploy = localDeploy ?? (options => serverCenter.DeployAsync(options));
        Sources =
        [
            new(ServerPackageSourceKind.OfficialStable, Text("server_center.wizard.source_official", "Official release")),
            new(ServerPackageSourceKind.LocalBundle, Text("server_center.wizard.source_local", "Local release bundle")),
            new(ServerPackageSourceKind.RemoteBundle, Text("server_center.wizard.source_server", "Bundle on this SSH server")),
            new(ServerPackageSourceKind.DirectUrl, Text("server_center.wizard.source_url", "Custom HTTPS download"))
        ];
        if (IsLocalInstallation) Sources = Sources.Where(source => source.Source != ServerPackageSourceKind.RemoteBundle).ToArray();
        SelectedSource = Sources[0];

        Modes = BuildModes(IsLocalInstallation ? HostPlatformKind.Windows : serverCenter.SelectedPlatform?.Platform);
        FileAccesses =
        [
            new(ServerFileAccessScope.Restricted, Text("server_center.wizard.file_access_restricted", "RelaxKonOS data only (recommended)")),
            new(ServerFileAccessScope.Whitelist, Text("server_center.wizard.file_access_whitelist", "Selected directories")),
            new(ServerFileAccessScope.Full, Text("server_center.wizard.file_access_full", "All local files"))
        ];
        SelectedFileAccess = FileAccesses[0];
        SelectedAdministratorFileAccess = FileAccesses[0];
        SelectedRootFileAccess = FileAccesses[0];
        Networks =
        [
            new(ServerNetworkProfile.Loopback, Text("server_center.wizard.network_loopback", "Local only (recommended)")),
            new(ServerNetworkProfile.Lan, Text("server_center.wizard.network_lan", "0.0.0.0 (all network interfaces)"))
        ];
        SelectedNetwork = Networks[0];
        CertificateModes =
        [
            new(ServerCertificateMode.None, Text("server_center.wizard.certificate_none", "No TLS certificate")),
            new(ServerCertificateMode.Custom, Text("server_center.wizard.certificate_custom", "Use a custom certificate")),
            new(ServerCertificateMode.SelfSigned, Text("server_center.wizard.certificate_self_signed", "Generate a self-signed certificate"))
        ];
        var installedUrl = serverCenter.SelectedHost?.LastVerified?.ListenUrl;
        SelectedCertificateMode = CertificateModes[installedUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true ? 2 : 0];
        if (Uri.TryCreate(installedUrl, UriKind.Absolute, out var installedUri)) ServerPortText = installedUri.Port.ToString();
        SelectedCertificateFormat = CertificateFormats[0];
        SelectedMode = Modes.FirstOrDefault(option => option.Mode == serverCenter.SelectedHost?.LastVerified?.Mode) ?? Modes[0];
    }

    public IReadOnlyList<InstallationSourceOption> Sources { get; }
    public IReadOnlyList<InstallationModeOption> Modes { get; }
    public IReadOnlyList<FileAccessOption> FileAccesses { get; }
    public IReadOnlyList<NetworkOption> Networks { get; }
    public IReadOnlyList<CertificateModeOption> CertificateModes { get; }
    public IReadOnlyList<CertificateFormatOption> CertificateFormats { get; } =
    [
        new(ServerCertificateFormat.Pfx, "PFX / P12"),
        new(ServerCertificateFormat.Pem, "PEM certificate chain + private key")
    ];

    [ObservableProperty] private int _stepIndex;
    [ObservableProperty] private InstallationSourceOption? _selectedSource;
    [ObservableProperty] private InstallationModeOption? _selectedMode;
    [ObservableProperty] private string? _localBundlePath;
    [ObservableProperty] private string? _remoteBundlePath;
    [ObservableProperty] private FileAccessOption? _selectedFileAccess;
    [ObservableProperty] private NetworkOption? _selectedNetwork;
    [ObservableProperty] private CertificateModeOption? _selectedCertificateMode;
    [ObservableProperty] private CertificateFormatOption? _selectedCertificateFormat;
    [ObservableProperty] private string? _certificatePath;
    [ObservableProperty] private string? _certificatePrivateKeyPath;
    [ObservableProperty] private string _certificatePassword = string.Empty;
    [ObservableProperty] private string _sudoPassword = string.Empty;
    [ObservableProperty] private string _selfSignedIdentities = "localhost,127.0.0.1";
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _serverPortText = "5000";
    [ObservableProperty] private string? _packageUri;
    [ObservableProperty] private string? _packageDigest;
    [ObservableProperty] private string? _releaseCatalogBaseUri;
    [ObservableProperty] private string? _installRoot;
    [ObservableProperty] private string? _dataRoot;
    [ObservableProperty] private string? _configRoot;
    [ObservableProperty] private string? _stateRoot;
    [ObservableProperty] private string? _cacheRoot;
    [ObservableProperty] private string? _fileRoots;
    [ObservableProperty] private string? _administratorFileRoots;
    [ObservableProperty] private string? _rootFileRoots;
    [ObservableProperty] private FileAccessOption? _selectedAdministratorFileAccess;
    [ObservableProperty] private FileAccessOption? _selectedRootFileAccess;
    [ObservableProperty] private bool _addFirewallRule;
    public string FirewallChoiceText => Text("server_center.firewall_choice", "Add an inbound firewall rule for the server TCP port");
    public string FirewallHelpText => Text("server_center.firewall_help", "Applies to network access. If the host firewall is disabled, you will be notified and no rule will be added.");
    [ObservableProperty] private bool _dockerAccess;
    [ObservableProperty] private bool _allowUnsupportedSystem;
    public bool IsOfficialSource => SelectedSource?.Source == ServerPackageSourceKind.OfficialStable;
    public bool IsDirectUrl => SelectedSource?.Source == ServerPackageSourceKind.DirectUrl;
    public bool IsSystemMode => SelectedMode?.Mode is not (ServerInstallMode.LinuxUser or ServerInstallMode.WindowsUser);
    public bool IsWindowsUserMode => SelectedMode?.Mode == ServerInstallMode.WindowsUser;
    public bool CanEditDataRoot => !IsWindowsUserMode;
    public string PersonalModeHint => Text("server_center.wizard.windows_personal_hint", "Runs as your current Windows account, starts at sign-in and stops at sign-out. Docker uses your account permissions. Separate program and data in LocalAppData; privileged host operations are unavailable.");
    public bool IsUserMode => SelectedMode?.Mode == ServerInstallMode.LinuxUser;
    public bool IsLinuxSystemMode => SelectedMode?.Mode == ServerInstallMode.LinuxSystem;
    public bool IsLinuxHost => _serverCenter.SelectedPlatform?.Platform == HostPlatformKind.Linux;
    public bool IsFileWhitelist => SelectedFileAccess?.Scope == ServerFileAccessScope.Whitelist;
    public bool IsAdministratorWhitelist => SelectedAdministratorFileAccess?.Scope == ServerFileAccessScope.Whitelist;
    public bool IsRootWhitelist => SelectedRootFileAccess?.Scope == ServerFileAccessScope.Whitelist;
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static IReadOnlyList<string>? Roots(string? value) => Optional(value) is null ? null : value!.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool IsSourceStep => StepIndex == 0;
    public bool IsModeStep => StepIndex == 1;
    public bool IsReviewStep => StepIndex == 2;
    public bool IsLocalBundle => SelectedSource?.Source == ServerPackageSourceKind.LocalBundle;
    public bool IsRemoteBundle => SelectedSource?.Source == ServerPackageSourceKind.RemoteBundle;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string StepCounter => string.Format(Text("server_center.wizard.step_counter", "Step {0} of 3"), StepIndex + 1);
    public string BundleFileName => Path.GetFileName(IsRemoteBundle ? RemoteBundlePath : LocalBundlePath) ?? string.Empty;
    public bool HasBundle => IsRemoteBundle ? !string.IsNullOrWhiteSpace(RemoteBundlePath) : !string.IsNullOrWhiteSpace(LocalBundlePath);
    public string SelectedSourceText => (IsLocalBundle || IsRemoteBundle) && HasBundle
        ? $"{SelectedSource?.Label} · {BundleFileName}"
        : IsDirectUrl ? $"{SelectedSource?.Label} · {PackageUri}" : SelectedSource?.Label ?? string.Empty;
    public string SelectedModeText => SelectedMode?.Label ?? string.Empty;
    public bool ShowsSudoPassword => SelectedMode?.Mode == ServerInstallMode.LinuxSystem;
    public string SudoPasswordText => Text("server_center.wizard.sudo_password", "sudo password (leave blank to use the SSH password)");
    public string TargetText => IsLocalInstallation ? Environment.MachineName : _serverCenter.SelectedHost is { } target
        ? $"{target.DisplayName} · {target.SshUserName}"
        : string.Empty;

    public string AdvancedLabel => Text("server_center.wizard.advanced", "Advanced installation options");
    public string ServerPortLabel => Text("server_center.wizard.server_port", "Server port (1–65535)");
    public string PackageUriLabel => Text("server_center.wizard.package_uri", "Release ZIP HTTPS URL");
    public string PackageDigestLabel => Text("server_center.wizard.package_digest", "Release ZIP SHA-256");
    public string ReleaseCatalogLabel => Text("server_center.wizard.release_catalog", "Release catalog HTTPS base (blank = official)");
    public string InstallRootLabel => Text("server_center.wizard.install_root", "Program directory (blank = default)");
    public string DataRootLabel => Text("server_center.wizard.data_root", "Data directory (blank = default)");
    public string ConfigRootLabel => Text("server_center.wizard.config_root", "Configuration directory (User Mode)");
    public string StateRootLabel => Text("server_center.wizard.state_root", "State directory (User Mode)");
    public string CacheRootLabel => Text("server_center.wizard.cache_root", "Cache directory (User Mode)");
    public string FileRootsLabel => Text("server_center.wizard.file_roots", "Allowed absolute directories, one per line");
    public string AdministratorAccessLabel => Text("server_center.wizard.administrator_access", "Administrator file access");
    public string RootAccessLabel => Text("server_center.wizard.root_access", "Root file access");
    public string DockerAccessLabel => Text("server_center.wizard.docker_access", "Authorize server access to Docker (Linux System)");
    public string AllowUnsupportedLabel => Text("server_center.wizard.allow_unsupported", "Allow a Linux system outside the supported matrix");
    public string OptionsInvalidLabel => Text("server_center.wizard.options_invalid", "Check the port, HTTPS URL, SHA-256 and whitelist directories.");
    public string SourceUrlLabel => Text("server_center.wizard.source_url", "Custom HTTPS download");
    public string WhitelistLabel => Text("server_center.wizard.file_access_whitelist", "Selected directories");
    public bool CanAddFirewallRule => IsSystemMode && SelectedNetwork?.Profile == ServerNetworkProfile.Lan;
    partial void OnSelectedNetworkChanged(NetworkOption? value) { OnPropertyChanged(nameof(CanAddFirewallRule)); if (!CanAddFirewallRule) AddFirewallRule = false; }
    public string AdvancedReviewText => string.Join("\n", new[] {
        IsSystemMode ? $"{FirewallChoiceText}: {CanAddFirewallRule && AddFirewallRule}" : string.Empty,
        $"{ServerPortLabel}: {ServerPortText}", $"{DataRootLabel}: {DataRoot}",
        IsSystemMode ? $"{InstallRootLabel}: {InstallRoot}" : $"{ConfigRootLabel}: {ConfigRoot}\n{StateRootLabel}: {StateRoot}\n{CacheRootLabel}: {CacheRoot}",
        IsFileWhitelist ? $"{FileRootsLabel}: {FileRoots}" : string.Empty,
        IsLinuxSystemMode ? $"{AdministratorAccessLabel}: {SelectedAdministratorFileAccess?.Label}\n{AdministratorFileRoots}\n{RootAccessLabel}: {SelectedRootFileAccess?.Label}\n{RootFileRoots}\n{DockerAccessLabel}: {DockerAccess}" : string.Empty,
        IsLinuxHost ? $"{AllowUnsupportedLabel}: {AllowUnsupportedSystem}" : string.Empty,
        IsOfficialSource ? $"{ReleaseCatalogLabel}: {ReleaseCatalogBaseUri}" : string.Empty, IsDirectUrl ? $"SHA-256: {PackageDigest}" : string.Empty
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string Title => IsLocalInstallation ? Text("login.local_install", "Install on this computer") : Text("server_center.wizard.title", "Install RelaxKonOS");
    public ServerCenterViewModel Progress => _serverCenter;
    public string SourceStepTitle => Text("server_center.wizard.source_title", "Choose the release source");
    public string ModeStepTitle => Text("server_center.wizard.mode_title", "Choose the installation mode");
    public string SourceChecksText => IsWindowsUserMode ? PersonalModeHint : IsLocalInstallation
        ? Text("login.local_install_review", "Windows will request administrator permission when you install. Server runs as a system service on this computer. Official packages are verified; selected ZIPs receive layout and architecture checks.")
        : Text("server_center.wizard.source_checks", "Official packages are downloaded and verified on the server. Selected ZIPs receive layout and architecture checks.");
    public string ReviewStepTitle => Text("server_center.wizard.review_title", "Review and install");
    public string ServerAndUserText => Text("server_center.wizard.server_and_user", "Server and user");
    public string ReleaseText => Text("server_center.wizard.release", "Release");
    public string ModeText => Text("server_center.wizard.mode", "Mode");
    public string FileAccessText => Text("server_center.wizard.file_access", "Privileged file access");
    public string NetworkText => Text("server_center.wizard.network", "Network access");
    public string ShowHostAddressesText => Text("server_center.wizard.show_host_addresses", "View this host's IP addresses");
    public string CertificateText => Text("server_center.wizard.certificate", "TLS certificate");
    public string CertificateFormatText => Text("server_center.wizard.certificate_format", "Certificate format");
    public string CertificateFileText => Text("server_center.wizard.certificate_file", "Certificate file");
    public string CertificatePrivateKeyFileText => Text("server_center.wizard.certificate_private_key_file", "Private key file");
    public string ChooseCertificateText => IsPemCertificate
        ? Text("server_center.wizard.choose_pem_certificate", "Choose PEM certificate chain")
        : Text("server_center.wizard.choose_certificate", "Choose PFX certificate");
    public string ChoosePrivateKeyText => Text("server_center.wizard.choose_private_key", "Choose PEM private key");
    public bool IsCustomCertificate => SelectedCertificateMode?.Mode == ServerCertificateMode.Custom;
    public bool IsSelfSignedCertificate => SelectedCertificateMode?.Mode == ServerCertificateMode.SelfSigned;
    public bool IsPemCertificate => SelectedCertificateFormat?.Format == ServerCertificateFormat.Pem;
    public string SelfSignedNamesText => Text("server_center.wizard.certificate_names", "Certificate names (comma-separated)");
    public bool HasCertificate => !string.IsNullOrWhiteSpace(CertificatePath) &&
        (!IsPemCertificate || !string.IsNullOrWhiteSpace(CertificatePrivateKeyPath));
    public string CertificateFileName => string.IsNullOrWhiteSpace(CertificatePath) ? string.Empty : Path.GetFileName(CertificatePath);
    public string CertificatePrivateKeyFileName => string.IsNullOrWhiteSpace(CertificatePrivateKeyPath)
        ? string.Empty : Path.GetFileName(CertificatePrivateKeyPath);
    public string LocalBundleText => Text("server_center.wizard.local_bundle", "Local release bundle");
    public string ChooseBundleText => Text("server_center.wizard.choose_bundle", "Choose bundle");
    public string ChooseServerBundleText => Text("server_center.wizard.choose_server_bundle", "Browse server files");
    public string ServerBundleText => Text("server_center.wizard.server_bundle", "Release bundle on this SSH server");
    public string BundleFileTypeText => Text("server_center.wizard.bundle_file_type", "RelaxKonOS release bundle");
    public string BundleRequiredText => Text("server_center.wizard.bundle_required", "Choose a .zip release bundle to continue.");
    public string BackText => Text("server_center.wizard.back", "Back");
    public string NextText => Text("server_center.wizard.next", "Next");
    public string CancelText => Text("server_center.wizard.cancel", "Cancel");
    public string InstallText => _serverCenter.DeployText;

    public void SetLocalBundle(string? path)
    {
        LocalBundlePath = string.IsNullOrWhiteSpace(path) ? null : path;
        ErrorMessage = string.Empty;
    }

    public void SetCertificate(string? path)
    {
        CertificatePath = string.IsNullOrWhiteSpace(path) ? null : path;
        ErrorMessage = string.Empty;
    }

    public void SetCertificatePrivateKey(string? path)
    {
        CertificatePrivateKeyPath = string.IsNullOrWhiteSpace(path) ? null : path;
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    private async Task ChooseServerBundleAsync()
    {
        var path = await _chooseServerBundle();
        if (string.IsNullOrWhiteSpace(path)) return;
        RemoteBundlePath = path;
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    private Task ShowHostAddressesAsync() => _showHostAddresses();

    [RelayCommand(CanExecute = nameof(CanMoveNext))]
    private void MoveNext()
    {
        if (IsSourceStep && (IsLocalBundle || IsRemoteBundle) && !HasBundle)
        {
            ErrorMessage = BundleRequiredText;
            return;
        }
        if (IsSourceStep && IsDirectUrl && (!Uri.TryCreate(PackageUri, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !ServerDeploymentInputRules.IsSha256(PackageDigest))) { ErrorMessage = OptionsInvalidLabel; return; }
        if (IsModeStep && (!int.TryParse(ServerPortText, out var port) || port is < 1 or > 65535 ||
            IsSystemMode && (IsFileWhitelist && Roots(FileRoots) is not { Count: > 0 } ||
            IsLinuxSystemMode && (IsAdministratorWhitelist && Roots(AdministratorFileRoots) is not { Count: > 0 } || IsRootWhitelist && Roots(RootFileRoots) is not { Count: > 0 })))) { ErrorMessage = OptionsInvalidLabel; return; }
        ErrorMessage = string.Empty;
        StepIndex++;
    }

    [RelayCommand(CanExecute = nameof(CanMoveBack))]
    private void MoveBack()
    {
        if (StepIndex > 0) StepIndex--;
    }

    [RelayCommand]
    private void Cancel()
    {
        SudoPassword = string.Empty;
        CertificatePassword = string.Empty;
        _close();
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if ((IsLocalBundle || IsRemoteBundle) && !HasBundle)
        {
            ErrorMessage = BundleRequiredText;
            return;
        }
        if (IsCustomCertificate && !HasCertificate)
        {
            ErrorMessage = Text("server_center.wizard.certificate_required", "Choose the certificate files to continue.");
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        var succeeded = false;
        try
        {
            succeeded = await _deploy(new ServerInstallationOptions(
                SelectedSource?.Source ?? ServerPackageSourceKind.OfficialStable,
                SelectedMode?.Mode,
                LocalBundlePath,
                RemoteBundlePath,
                SelectedFileAccess?.Scope ?? ServerFileAccessScope.Restricted,
                SelectedNetwork?.Profile ?? ServerNetworkProfile.Loopback,
                SelectedCertificateMode?.Mode ?? ServerCertificateMode.None,
                SelectedCertificateFormat?.Format ?? ServerCertificateFormat.Pfx,
                CertificatePath,
                CertificatePrivateKeyPath,
                CertificatePassword,
                SelfSignedIdentities,
                SudoPassword, int.Parse(ServerPortText), Optional(PackageUri), Optional(PackageDigest), IsOfficialSource ? Optional(ReleaseCatalogBaseUri) : null,
                Optional(InstallRoot), Optional(DataRoot), Optional(ConfigRoot), Optional(StateRoot), Optional(CacheRoot),
                IsFileWhitelist ? Roots(FileRoots) : null, SelectedAdministratorFileAccess?.Scope,
                IsAdministratorWhitelist ? Roots(AdministratorFileRoots) : null, SelectedRootFileAccess?.Scope,
                IsRootWhitelist ? Roots(RootFileRoots) : null, DockerAccess, AllowUnsupportedSystem, IsSystemMode && SelectedNetwork?.Profile == ServerNetworkProfile.Lan && AddFirewallRule));
            if (!succeeded)
                ErrorMessage = string.IsNullOrWhiteSpace(_serverCenter.ErrorMessage)
                    ? Text("server_center.wizard.install_blocked", "Installation is blocked until the SSH host key is confirmed on the Hosts page.")
                    : _serverCenter.ErrorMessage;
        }
        finally
        {
            SudoPassword = string.Empty;
            if (succeeded) CertificatePassword = string.Empty;
            IsBusy = false;
        }
        if (succeeded) _close();
    }

    private bool CanMoveNext() => !IsBusy && StepIndex < 2;
    private bool CanMoveBack() => !IsBusy && StepIndex > 0;
    private bool CanInstall() => !IsBusy && IsReviewStep;

    partial void OnStepIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSourceStep));
        OnPropertyChanged(nameof(IsModeStep));
        OnPropertyChanged(nameof(IsReviewStep));
        OnPropertyChanged(nameof(StepCounter));
        OnPropertyChanged(nameof(AdvancedReviewText));
        OnPropertyChanged(nameof(SelectedSourceText));
        MoveNextCommand.NotifyCanExecuteChanged();
        MoveBackCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedSourceChanged(InstallationSourceOption? value)
    {
        ErrorMessage = string.Empty;
        OnPropertyChanged(nameof(IsOfficialSource));
        OnPropertyChanged(nameof(AdvancedReviewText));
        OnPropertyChanged(nameof(SelectedSourceText));
        OnPropertyChanged(nameof(IsLocalBundle));
        OnPropertyChanged(nameof(IsDirectUrl));
        OnPropertyChanged(nameof(IsRemoteBundle));
        OnPropertyChanged(nameof(BundleFileName));
        OnPropertyChanged(nameof(HasBundle));
    }

    partial void OnSelectedModeChanged(InstallationModeOption? value)
    {
        OnPropertyChanged(nameof(IsWindowsUserMode)); OnPropertyChanged(nameof(CanEditDataRoot)); OnPropertyChanged(nameof(SourceChecksText));
        if (value?.Mode == ServerInstallMode.WindowsUser) { InstallRoot = string.Empty; DataRoot = string.Empty; AddFirewallRule = false; SelectedFileAccess = FileAccesses[0]; }
        OnPropertyChanged(nameof(CanAddFirewallRule));
        OnPropertyChanged(nameof(SelectedModeText));
        OnPropertyChanged(nameof(ShowsSudoPassword));
        OnPropertyChanged(nameof(IsSystemMode)); OnPropertyChanged(nameof(IsUserMode)); OnPropertyChanged(nameof(IsLinuxSystemMode));
        if (value?.Mode == ServerInstallMode.LinuxUser) { SelectedNetwork = Networks[0]; SelectedCertificateMode = CertificateModes[0]; SelectedFileAccess = FileAccesses[0]; }
    }

    partial void OnSelectedFileAccessChanged(FileAccessOption? value) => OnPropertyChanged(nameof(IsFileWhitelist));
    partial void OnSelectedAdministratorFileAccessChanged(FileAccessOption? value) => OnPropertyChanged(nameof(IsAdministratorWhitelist));
    partial void OnSelectedRootFileAccessChanged(FileAccessOption? value) => OnPropertyChanged(nameof(IsRootWhitelist));
    partial void OnSelectedCertificateModeChanged(CertificateModeOption? value)
    {
        OnPropertyChanged(nameof(IsCustomCertificate));
        OnPropertyChanged(nameof(IsSelfSignedCertificate));
    }

    partial void OnCertificatePathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasCertificate));
        OnPropertyChanged(nameof(CertificateFileName));
    }

    partial void OnCertificatePrivateKeyPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasCertificate));
        OnPropertyChanged(nameof(CertificatePrivateKeyFileName));
    }

    partial void OnSelectedCertificateFormatChanged(CertificateFormatOption? value)
    {
        OnPropertyChanged(nameof(IsPemCertificate));
        OnPropertyChanged(nameof(ChooseCertificateText));
        OnPropertyChanged(nameof(HasCertificate));
    }

    partial void OnLocalBundlePathChanged(string? value)
    {
        OnPropertyChanged(nameof(SelectedSourceText));
        OnPropertyChanged(nameof(BundleFileName));
        OnPropertyChanged(nameof(HasBundle));
    }

    partial void OnRemoteBundlePathChanged(string? value)
    {
        OnPropertyChanged(nameof(SelectedSourceText));
        OnPropertyChanged(nameof(BundleFileName));
        OnPropertyChanged(nameof(HasBundle));
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnIsBusyChanged(bool value)
    {
        MoveNextCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
        MoveBackCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<InstallationModeOption> BuildModes(HostPlatformKind? platform) => platform switch
    {
        HostPlatformKind.Windows =>
        IsLocalInstallation
            ? [new(ServerInstallMode.WindowsUser, Text("server_center.wizard.mode_windows_user", "Personal mode (current Windows account)")),
               new(ServerInstallMode.WindowsSystem, Text("server_center.wizard.mode_windows_system", "Windows system service"))]
            : [new(ServerInstallMode.WindowsSystem, Text("server_center.wizard.mode_windows_system", "Windows system service"))],
        HostPlatformKind.Linux =>
        [
            new(null, Text("server_center.wizard.mode_automatic", "Automatic (recommended after preflight)")),
            new(ServerInstallMode.LinuxUser, Text("server_center.wizard.mode_linux_user", "Linux user mode")),
            new(ServerInstallMode.LinuxSystem, Text("server_center.wizard.mode_linux_system", "Linux system service"))
        ],
        _ => [new(null, Text("server_center.wizard.mode_automatic", "Automatic (recommended after preflight)"))]
    };

    private string Text(string key, string fallback) => _serverCenter.Text(key, fallback);
}

public sealed record InstallationSourceOption(ServerPackageSourceKind Source, string Label);
public sealed record InstallationModeOption(ServerInstallMode? Mode, string Label);
public sealed record FileAccessOption(ServerFileAccessScope Scope, string Label);
public sealed record NetworkOption(ServerNetworkProfile Profile, string Label);
public sealed record CertificateModeOption(ServerCertificateMode Mode, string Label);
public enum ServerCertificateFormat { Pfx, Pem }
public sealed record CertificateFormatOption(ServerCertificateFormat Format, string Label);
