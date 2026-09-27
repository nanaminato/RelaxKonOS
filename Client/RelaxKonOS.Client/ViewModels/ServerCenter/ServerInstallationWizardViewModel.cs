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

    public ServerInstallationWizardViewModel(ServerCenterViewModel serverCenter, Action close,
        Func<Task<string?>> chooseServerBundle)
    {
        _serverCenter = serverCenter;
        _close = close;
        _chooseServerBundle = chooseServerBundle;
        Sources =
        [
            new(ServerPackageSourceKind.OfficialStable, Text("server_center.wizard.source_official", "Trusted official release")),
            new(ServerPackageSourceKind.LocalBundle, Text("server_center.wizard.source_local", "Trusted local bundle")),
            new(ServerPackageSourceKind.RemoteBundle, Text("server_center.wizard.source_server", "Bundle on this SSH server"))
        ];
        SelectedSource = Sources[0];

        Modes = BuildModes(serverCenter.SelectedPlatform?.Platform);
        SelectedMode = Modes[0];
        FileAccesses =
        [
            new(ServerFileAccessScope.Restricted, Text("server_center.wizard.file_access_restricted", "RelaxKonOS data only (recommended)")),
            new(ServerFileAccessScope.Full, Text("server_center.wizard.file_access_full", "All local files"))
        ];
        SelectedFileAccess = FileAccesses[0];
        Networks =
        [
            new(ServerNetworkProfile.Loopback, Text("server_center.wizard.network_loopback", "Local only (recommended)")),
            new(ServerNetworkProfile.Lan, Text("server_center.wizard.network_lan", "LAN HTTP")),
            new(ServerNetworkProfile.ReverseProxy, Text("server_center.wizard.network_reverse_proxy", "Reverse proxy"))
        ];
        SelectedNetwork = Networks[0];
    }

    public IReadOnlyList<InstallationSourceOption> Sources { get; }
    public IReadOnlyList<InstallationModeOption> Modes { get; }
    public IReadOnlyList<FileAccessOption> FileAccesses { get; }
    public IReadOnlyList<NetworkOption> Networks { get; }

    [ObservableProperty] private int _stepIndex;
    [ObservableProperty] private InstallationSourceOption? _selectedSource;
    [ObservableProperty] private InstallationModeOption? _selectedMode;
    [ObservableProperty] private string? _localBundlePath;
    [ObservableProperty] private string? _remoteBundlePath;
    [ObservableProperty] private FileAccessOption? _selectedFileAccess;
    [ObservableProperty] private NetworkOption? _selectedNetwork;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public bool IsSourceStep => StepIndex == 0;
    public bool IsModeStep => StepIndex == 1;
    public bool IsReviewStep => StepIndex == 2;
    public bool IsLocalBundle => SelectedSource?.Source == ServerPackageSourceKind.LocalBundle;
    public bool IsRemoteBundle => SelectedSource?.Source == ServerPackageSourceKind.RemoteBundle;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string StepCounter => string.Format(Text("server_center.wizard.step_counter", "Step {0} of 3"), StepIndex + 1);
    public string BundleFileName => Path.GetFileName(IsRemoteBundle ? RemoteBundlePath : LocalBundlePath) ?? string.Empty;
    public bool HasBundle => IsRemoteBundle ? !string.IsNullOrWhiteSpace(RemoteBundlePath) : !string.IsNullOrWhiteSpace(LocalBundlePath);
    public string SelectedSourceText => SelectedSource?.Label ?? string.Empty;
    public string SelectedModeText => SelectedMode?.Label ?? string.Empty;
    public string TargetText => _serverCenter.SelectedHost is { } target
        ? $"{target.DisplayName} · {target.SshUserName}"
        : string.Empty;

    public string Title => Text("server_center.wizard.title", "Install RelaxKonOS");
    public string SourceStepTitle => Text("server_center.wizard.source_title", "Choose the release source");
    public string ModeStepTitle => Text("server_center.wizard.mode_title", "Choose the installation mode");
    public string ReviewStepTitle => Text("server_center.wizard.review_title", "Review and install");
    public string ServerAndUserText => Text("server_center.wizard.server_and_user", "Server and user");
    public string ReleaseText => Text("server_center.wizard.release", "Release");
    public string ModeText => Text("server_center.wizard.mode", "Mode");
    public string FileAccessText => Text("server_center.wizard.file_access", "Privileged file access");
    public string NetworkText => Text("server_center.wizard.network", "Network access");
    public string LocalBundleText => Text("server_center.wizard.local_bundle", "Signed local release bundle");
    public string ChooseBundleText => Text("server_center.wizard.choose_bundle", "Choose bundle");
    public string ChooseServerBundleText => Text("server_center.wizard.choose_server_bundle", "Browse server files");
    public string ServerBundleText => Text("server_center.wizard.server_bundle", "Signed release bundle on this SSH server");
    public string BundleFileTypeText => Text("server_center.wizard.bundle_file_type", "RelaxKonOS signed release");
    public string BundleRequiredText => Text("server_center.wizard.bundle_required", "Choose a signed .zip release bundle to continue.");
    public string BackText => Text("server_center.wizard.back", "Back");
    public string NextText => Text("server_center.wizard.next", "Next");
    public string CancelText => Text("server_center.wizard.cancel", "Cancel");
    public string InstallText => _serverCenter.DeployText;

    public void SetLocalBundle(string? path)
    {
        LocalBundlePath = string.IsNullOrWhiteSpace(path) ? null : path;
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

    [RelayCommand(CanExecute = nameof(CanMoveNext))]
    private void MoveNext()
    {
        if (IsSourceStep && (IsLocalBundle || IsRemoteBundle) && !HasBundle)
        {
            ErrorMessage = BundleRequiredText;
            return;
        }
        StepIndex++;
    }

    [RelayCommand]
    private void MoveBack()
    {
        if (StepIndex > 0) StepIndex--;
    }

    [RelayCommand]
    private void Cancel() => _close();

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if ((IsLocalBundle || IsRemoteBundle) && !HasBundle)
        {
            ErrorMessage = BundleRequiredText;
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var succeeded = await _serverCenter.DeployAsync(new ServerInstallationOptions(
                SelectedSource?.Source ?? ServerPackageSourceKind.OfficialStable,
                SelectedMode?.Mode,
                LocalBundlePath,
                RemoteBundlePath,
                SelectedFileAccess?.Scope ?? ServerFileAccessScope.Restricted,
                SelectedNetwork?.Profile ?? ServerNetworkProfile.Loopback));
            if (succeeded)
                _close();
            else
                ErrorMessage = string.IsNullOrWhiteSpace(_serverCenter.ErrorMessage)
                    ? Text("server_center.wizard.install_blocked", "Installation is blocked until the SSH host key is confirmed on the Hosts page.")
                    : _serverCenter.ErrorMessage;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanMoveNext() => !IsBusy && StepIndex < 2;
    private bool CanInstall() => !IsBusy && IsReviewStep;

    partial void OnStepIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSourceStep));
        OnPropertyChanged(nameof(IsModeStep));
        OnPropertyChanged(nameof(IsReviewStep));
        OnPropertyChanged(nameof(StepCounter));
        MoveNextCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedSourceChanged(InstallationSourceOption? value)
    {
        ErrorMessage = string.Empty;
        OnPropertyChanged(nameof(IsLocalBundle));
        OnPropertyChanged(nameof(IsRemoteBundle));
        OnPropertyChanged(nameof(BundleFileName));
        OnPropertyChanged(nameof(HasBundle));
    }

    partial void OnLocalBundlePathChanged(string? value)
    {
        OnPropertyChanged(nameof(BundleFileName));
        OnPropertyChanged(nameof(HasBundle));
    }

    partial void OnRemoteBundlePathChanged(string? value)
    {
        OnPropertyChanged(nameof(BundleFileName));
        OnPropertyChanged(nameof(HasBundle));
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnIsBusyChanged(bool value)
    {
        MoveNextCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<InstallationModeOption> BuildModes(HostPlatformKind? platform) => platform switch
    {
        HostPlatformKind.Windows =>
        [new(ServerInstallMode.WindowsSystem, Text("server_center.wizard.mode_windows_system", "Windows system service"))],
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
