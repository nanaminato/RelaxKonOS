using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Services.ServerCenter;

namespace RelaxKonOS.Client.ViewModels.Login;

public partial class LoginViewModel
{
    public bool LocalInstallationAvailable => OperatingSystem.IsWindows();
    public string LocalInstallText => T("login.local_install", "Manage this computer");
    public Func<Task<string?>>? ShowLocalInstallationAsync { get; set; }

    [RelayCommand(CanExecute = nameof(CanInstallOnThisComputer))]
    private async Task InstallOnThisComputerAsync()
    {
        if (ShowLocalInstallationAsync is null) return;
        string? endpoint = null;
        IsConnecting = true;
        ClearError();
        try
        {
            endpoint = await ShowLocalInstallationAsync();
            if (endpoint is not null) await UseInstalledLocalServerAsync(endpoint);
        }
        catch (Exception)
        {
            ErrorMessage = T("login.local_install_open_failed", "Unable to open local installation. Check that this is the Windows desktop client.");
            HasError = true;
        }
        finally { IsConnecting = false; }
        if (endpoint is null) return;
        // Resolve the API and keep the existing certificate-review flow; authentication remains explicit.
        await DiscoverServerEndpointAsync();
        if (!HasError) StatusMessage = T("login.local_install_ready", "Connected to the local server. Sign in with your Windows account password (not a Hello PIN).");
    }

    public async Task UseInstalledLocalServerAsync(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("A local HTTP(S) endpoint is required.", nameof(endpoint));
        await ReleaseLoginTunnelAsync();
        UseSshLogin = false;
        UseLoginTunnel = false;
        SelectedTunnel = null;
        SelectedProfile = null;
        ShowOwnerDeviceOptions = false;
        ShowOptions = true;
        ServerUrl = endpoint;
        Identifier = Environment.UserDomainName + "\\" + Environment.UserName;
        Password = string.Empty;
        OwnerDeviceKeyPassphrase = OwnerDevicePairingCode = string.Empty;
        ClearError();
    }

    private bool CanInstallOnThisComputer() => LocalInstallationAvailable && !IsConnecting && !IsDiscoveringServer;
}
