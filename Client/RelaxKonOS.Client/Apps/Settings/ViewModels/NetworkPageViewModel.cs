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
    }

    public override string Route => "network";
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

    /// <summary>Latency measurement state. The displayed text is derived so it re-localizes on a language switch.</summary>
    private enum LatencyState { NotTested, CannotTest, Testing, Measured, Failed }

    private LatencyState _latencyState = LatencyState.NotTested;
    private long _latencyMilliseconds;
    private string? _latencyFailure;

    /// <summary>Server address loading state. The displayed text is derived so it re-localizes on a language switch.</summary>
    private enum AddressesState { NotLoaded, NotConnected, Loading, Empty, Loaded, Failed }

    private AddressesState _addressesState = AddressesState.NotLoaded;
    private int _addressCount;
    private string? _addressesFailure;

    public string LatencyText => _latencyState switch
    {
        LatencyState.CannotTest => T("settings.network.cannot_test", "Not connected; unable to test."),
        LatencyState.Testing => T("settings.network.testing", "Testing…"),
        LatencyState.Measured => $"{_latencyMilliseconds} ms",
        LatencyState.Failed => string.Format(T("settings.network.test_failed", "Failed: {0}"), _latencyFailure),
        _ => T("settings.network.not_tested", "Not tested"),
    };

    public string ServerAddressesStatus => _addressesState switch
    {
        AddressesState.NotConnected => T("settings.network.not_connected", "Not connected to the server."),
        AddressesState.Loading => T("settings.network.loading_addresses", "Loading server addresses…"),
        AddressesState.Empty => T("settings.network.no_addresses", "No non-loopback IPv4 or IPv6 addresses were found."),
        AddressesState.Loaded => string.Format(T("settings.network.addresses_found", "{0} server addresses found."), _addressCount),
        AddressesState.Failed => string.Format(T("settings.network.addresses_failed", "Unable to get server addresses: {0}"), _addressesFailure),
        _ => T("settings.network.not_loaded", "Server addresses have not been loaded."),
    };

    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private bool _isLoadingServerAddresses;

    public async Task LoadServerAddressesAsync()
    {
        if (!IsConnected)
        {
            ServerAddresses.Clear();
            _addressesState = AddressesState.NotConnected;
            OnPropertyChanged(nameof(ServerAddressesStatus));
            return;
        }

        IsLoadingServerAddresses = true;
        _addressesState = AddressesState.Loading;
        OnPropertyChanged(nameof(ServerAddressesStatus));
        try
        {
            var addresses = await _system.GetNetworkAddressesAsync();
            ServerAddresses.Clear();
            foreach (var address in addresses)
                ServerAddresses.Add(address);
            _addressCount = addresses.Count;
            _addressesState = addresses.Count == 0 ? AddressesState.Empty : AddressesState.Loaded;
        }
        catch (Exception ex)
        {
            ServerAddresses.Clear();
            _addressesFailure = ex.Message;
            _addressesState = AddressesState.Failed;
        }
        finally
        {
            IsLoadingServerAddresses = false;
            OnPropertyChanged(nameof(ServerAddressesStatus));
        }
    }

    [RelayCommand]
    private Task RefreshServerAddressesAsync() => LoadServerAddressesAsync();

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestConnectionAsync()
    {
        if (_session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens })
        {
            _latencyState = LatencyState.CannotTest;
            OnPropertyChanged(nameof(LatencyText));
            return;
        }

        IsTesting = true;
        _latencyState = LatencyState.Testing;
        OnPropertyChanged(nameof(LatencyText));
        try
        {
            var sw = Stopwatch.StartNew();
            await _remote.GetMeAsync(url, tokens.AccessToken);
            sw.Stop();
            _latencyMilliseconds = sw.ElapsedMilliseconds;
            _latencyState = LatencyState.Measured;
        }
        catch (Exception ex)
        {
            _latencyFailure = ex.Message;
            _latencyState = LatencyState.Failed;
        }
        finally
        {
            IsTesting = false;
            OnPropertyChanged(nameof(LatencyText));
        }
    }

    private bool CanTest => !IsTesting;
    partial void OnIsTestingChanged(bool value) => TestConnectionCommand.NotifyCanExecuteChanged();
}
