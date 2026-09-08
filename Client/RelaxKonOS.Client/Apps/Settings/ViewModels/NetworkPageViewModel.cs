using System.Collections.ObjectModel;
using System.Diagnostics;
using RelaxKonOS.Client.Apps.TaskManager;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>Connection details, reachable server addresses, and a lightweight latency test.</summary>
public sealed partial class NetworkPageViewModel : SettingsPageViewModel
{
    private readonly IAuthSession _session;
    private readonly IRelaxKonOSClient _remote;
    private readonly ITaskManagerClient _system;

    public NetworkPageViewModel(
        ShellSettings settings,
        IAuthSession session,
        IRelaxKonOSClient remote,
        ITaskManagerClient system,
        Action? save)
        : base(settings, save)
    {
        _session = session;
        _remote = remote;
        _system = system;
        ServerAddresses = new ObservableCollection<NetworkAddressDto>();
        LatencyText = T("settings.network.not_tested", "Not tested");
        ServerAddressesStatus = T("settings.network.not_loaded", "Server addresses have not been loaded.");
    }

    public override string Glyph => "🌐";
    public override string DisplayNameKey => "settings.page.network";
    public override string DisplayName => "Network";

    public string ConnectionState => _session.State switch
    {
        AuthSessionState.Authenticated => T("settings.value.connected", "Connected"),
        AuthSessionState.Connecting => T("settings.value.connecting", "Connecting…"),
        _ => T("settings.value.not_connected", "Not connected"),
    };

    public string ServerUrl => _session.ServerUrl ?? "—";
    public string UserName => _session.CurrentUser?.Username ?? "—";
    public string WorkspaceName => _session.CurrentWorkspace?.Name ?? "—";
    public bool IsConnected => _session.State == AuthSessionState.Authenticated;
    public ObservableCollection<NetworkAddressDto> ServerAddresses { get; }

    [ObservableProperty] private string _latencyText = string.Empty;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string _serverAddressesStatus = string.Empty;
    [ObservableProperty] private bool _isLoadingServerAddresses;

    public async Task LoadServerAddressesAsync()
    {
        if (!IsConnected)
        {
            ServerAddresses.Clear();
            ServerAddressesStatus = T("settings.network.not_connected", "Not connected to the server.");
            return;
        }

        IsLoadingServerAddresses = true;
        ServerAddressesStatus = T("settings.network.loading_addresses", "Loading server addresses…");
        try
        {
            var addresses = await _system.GetNetworkAddressesAsync();
            ServerAddresses.Clear();
            foreach (var address in addresses)
                ServerAddresses.Add(address);
            ServerAddressesStatus = addresses.Count == 0
                ? T("settings.network.no_addresses", "No non-loopback IPv4 or IPv6 addresses were found.")
                : string.Format(T("settings.network.addresses_found", "{0} server addresses found."), addresses.Count);
        }
        catch (Exception ex)
        {
            ServerAddresses.Clear();
            ServerAddressesStatus = string.Format(T("settings.network.addresses_failed", "Unable to get server addresses: {0}"), ex.Message);
        }
        finally
        {
            IsLoadingServerAddresses = false;
        }
    }

    [RelayCommand]
    private Task RefreshServerAddressesAsync() => LoadServerAddressesAsync();

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestConnectionAsync()
    {
        if (_session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens })
        {
            LatencyText = T("settings.network.cannot_test", "Not connected; unable to test.");
            return;
        }

        IsTesting = true;
        LatencyText = T("settings.network.testing", "Testing…");
        try
        {
            var sw = Stopwatch.StartNew();
            await _remote.GetMeAsync(url, tokens.AccessToken);
            sw.Stop();
            LatencyText = $"{sw.ElapsedMilliseconds} ms";
        }
        catch (Exception ex)
        {
            LatencyText = string.Format(T("settings.network.test_failed", "Failed: {0}"), ex.Message);
        }
        finally
        {
            IsTesting = false;
        }
    }

    private bool CanTest => !IsTesting;
    partial void OnIsTestingChanged(bool value) => TestConnectionCommand.NotifyCanExecuteChanged();
}
