using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Client.Services.HostSettings;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

public sealed partial class HostNetworkEditorViewModel : ObservableObject, IDisposable
{
    private readonly IHostNetworkService _service;
    private readonly IAuthSession _session;
    private readonly LocalizationService _localization;
    private HostSettingsConnection? _connection;
    private Guid? _pendingOperation;
    private DateTimeOffset _confirmBefore;
    private readonly DispatcherTimer _confirmationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _disposed;
    private int _generation;

    public HostNetworkEditorViewModel(IHostNetworkService service, IAuthSession session, LocalizationService localization)
    {
        _service = service; _session = session; _localization = localization;
        session.StateChanged += OnSessionChanged;
        localization.LanguageChanged += OnLanguageChanged;
        _confirmationTimer.Tick += OnConfirmationTick;
    }
    public Func<HostSettingsConnection, Task<bool>>? RequestAuthorizationAsync { get; set; }
    public Action? RequestOpenAdapter { get; set; }
    public void OpenAdapter(NetworkAdapterItem adapter)
    {
        if (!SelectAdapterForNavigation(adapter)) return;
        RequestOpenAdapter?.Invoke();
    }
    public bool SelectAdapterForNavigation(NetworkAdapterItem adapter)
    {
        if (!Adapters.Contains(adapter) || _pendingOperation is not null && adapter != SelectedAdapter) return false;
        SelectedAdapter = adapter;
        return true;
    }
    public ObservableCollection<NetworkAdapterItem> Adapters { get; } = [];
    [ObservableProperty] private NetworkAdapterItem? _selectedAdapter;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _dhcp = true;
    [ObservableProperty] private bool _automaticDns = true;
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _prefixLength = "24";
    [ObservableProperty] private string _gateway = "";
    [ObservableProperty] private string _dnsServers = "";
    [ObservableProperty] private string _problem = "";
    public bool HasDetails => SelectedAdapter is not null;
    public bool HasProblem => Problem.Length > 0;
    public bool CanEdit => !IsBusy && _pendingOperation is null && SelectedAdapter?.Value.CanConfigure == true;
    public bool HasPendingConfirmation => _pendingOperation is not null;
    public bool CanApply => CanEdit && HasChanges;
    private bool HasChanges => SelectedAdapter is { } adapter && (Dhcp != adapter.Value.Dhcp
        || AutomaticDns != adapter.Value.AutomaticDns
        || !Dhcp && (Address.Trim() != adapter.Value.Addresses.FirstOrDefault(a => a.Address.Contains('.'))?.Address
            || PrefixLength != adapter.Value.Addresses.FirstOrDefault(a => a.Address.Contains('.'))?.PrefixLength.ToString()
            || Gateway.Trim() != (adapter.Value.Gateways.FirstOrDefault(g => g.Contains('.')) ?? ""))
        || !AutomaticDns && !DnsServers.Split([',', ';', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .SequenceEqual(adapter.Value.DnsServers.Where(d => d.Contains('.'))));
    public bool CanConfirm => !IsBusy && _pendingOperation is not null && DateTimeOffset.UtcNow < _confirmBefore;
    public string ConfirmationText => string.Format(LocalizedText.Get("settings.network.confirm_hint"),
        Math.Max(0, (int)(_confirmBefore - DateTimeOffset.UtcNow).TotalSeconds));
    public string UnavailableReason => SelectedAdapter?.Value.UnavailableReason is { } key ? LocalizedText.Get(key) : "";
    public bool HasUnavailableReason => UnavailableReason.Length > 0;
    public string Target => _session.EffectiveBaseUrl ?? "";

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));
    partial void OnDhcpChanged(bool value) => UpdateCommands();
    partial void OnAutomaticDnsChanged(bool value) => UpdateCommands();
    partial void OnAddressChanged(string value) => UpdateCommands();
    partial void OnPrefixLengthChanged(string value) => UpdateCommands();
    partial void OnGatewayChanged(string value) => UpdateCommands();
    partial void OnDnsServersChanged(string value) => UpdateCommands();
    partial void OnIsBusyChanged(bool value) => UpdateCommands();
    partial void OnSelectedAdapterChanged(NetworkAdapterItem? value)
    {
        if (value is not null)
        {
            Dhcp = value.Value.Dhcp; AutomaticDns = value.Value.AutomaticDns;
            var ipv4 = value.Value.Addresses.FirstOrDefault(a => a.Address.Contains('.'));
            Address = ipv4?.Address ?? ""; PrefixLength = (ipv4?.PrefixLength ?? 24).ToString();
            Gateway = value.Value.Gateways.FirstOrDefault(g => g.Contains('.')) ?? "";
            DnsServers = string.Join(", ", value.Value.DnsServers.Where(d => d.Contains('.')));
        }
        Problem = "";
        OnPropertyChanged(nameof(HasDetails)); OnPropertyChanged(nameof(UnavailableReason)); OnPropertyChanged(nameof(HasUnavailableReason));
        UpdateCommands();
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        if (IsBusy || _disposed) return;
        var generation = _generation;
        IsBusy = true; Problem = "";
        try
        {
            var connection = _service.CaptureConnection();
            var snapshot = await _service.ReadAsync(connection);
            if (_disposed || generation != _generation || !_service.IsCurrent(connection)) return;
            _connection = connection;
            var selectedId = SelectedAdapter?.Value.Id;
            Adapters.Clear();
            foreach (var adapter in snapshot.Adapters) Adapters.Add(new(adapter));
            SelectedAdapter = Adapters.FirstOrDefault(a => a.Value.Id == selectedId) ?? Adapters.FirstOrDefault();
            OnPropertyChanged(nameof(Target));
        }
        catch (Exception error) { if (generation == _generation) Failure(error, "settings.network.read_failed"); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (!CanApply || SelectedAdapter is not { } adapter || _connection is not { } connection) return;
        var generation = _generation;
        var dns = AutomaticDns ? [] : DnsServers.Split([',', ';', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var change = new HostNetworkChange(adapter.Value.Id, Dhcp, Dhcp ? null : Address.Trim(),
            int.TryParse(PrefixLength, out var prefix) ? prefix : 0, Dhcp || string.IsNullOrWhiteSpace(Gateway) ? null : Gateway.Trim(), AutomaticDns, dns);
        if (!HostNetworkValidation.IsValid(change)) { Problem = LocalizedText.Get("settings.network.invalid_configuration"); return; }
        IsBusy = true; Problem = "";
        try
        {
            if (RequestAuthorizationAsync is null || !await RequestAuthorizationAsync(connection)) return;
            if (_disposed || generation != _generation || !_service.IsCurrent(connection)) return;
            var operationId = Guid.NewGuid();
            // Retain the ID even if changing the address interrupts the HTTP response. Never replay the write.
            _pendingOperation = operationId; _confirmBefore = DateTimeOffset.UtcNow.AddSeconds(90);
            _confirmationTimer.Start(); UpdateCommands();
            var result = await _service.ApplyAsync(connection, new(operationId, adapter.Value.Revision, change));
            if (_disposed || generation != _generation) return;
            _confirmBefore = result.ConfirmBefore;
        }
        catch (Exception error) { if (generation == _generation) Failure(error, "settings.network.apply_failed"); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        if (_pendingOperation is not { } id || _connection is not { } connection) return;
        var generation = _generation;
        IsBusy = true;
        var reload = false;
        try
        {
            var result = await _service.ConfirmAsync(connection, id);
            if (_disposed || generation != _generation) return;
            if (!result.Confirmed) throw new InvalidOperationException();
            _pendingOperation = null; _confirmationTimer.Stop(); Problem = ""; reload = true;
        }
        catch (Exception error) { if (generation == _generation) Failure(error, "settings.network.confirm_expired"); }
        finally { IsBusy = false; }
        if (reload) await ReloadAsync();
    }

    private void Failure(Exception error, string key)
    {
        Problem = LocalizedText.Get(key);
        LanguageSwitchDiagnostics.Record("network.failed", new { operationId = _pendingOperation, exceptionType = error.GetType().Name });
    }
    private void OnConfirmationTick(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(ConfirmationText));
        if (DateTimeOffset.UtcNow >= _confirmBefore)
        {
            _confirmationTimer.Stop(); _pendingOperation = null;
            Problem = LocalizedText.Get("settings.network.confirm_expired");
        }
        UpdateCommands();
    }
    private void UpdateCommands()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(HasPendingConfirmation)); OnPropertyChanged(nameof(ConfirmationText));
        ApplyCommand.NotifyCanExecuteChanged(); ConfirmCommand.NotifyCanExecuteChanged();
    }
    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        foreach (var item in Adapters) item.Relocalize();
        OnPropertyChanged(nameof(UnavailableReason)); OnPropertyChanged(nameof(ConfirmationText));
    }
    private void OnSessionChanged(object? sender, AuthSessionStateChangedEventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || _connection is null || _service.IsCurrent(_connection)) return;
        _generation++; _connection = null; Adapters.Clear(); SelectedAdapter = null;
        _pendingOperation = null; _confirmationTimer.Stop(); UpdateCommands();
    });
    public void Dispose()
    {
        _disposed = true; _generation++; _confirmationTimer.Stop(); _confirmationTimer.Tick -= OnConfirmationTick;
        _session.StateChanged -= OnSessionChanged; _localization.LanguageChanged -= OnLanguageChanged;
    }
}

public sealed class NetworkAdapterItem(HostNetworkAdapter value) : ObservableObject
{
    public HostNetworkAdapter Value { get; } = value;
    public string Name => Value.Name;
    public bool IsWifi => Value.Kind == "wifi";
    public string Kind => LocalizedText.Get("settings.network.kind." + Value.Kind);
    public string Status => LocalizedText.Get(Value.Connected ? "settings.value.connected" : "settings.value.not_connected");
    public string Description => Value.Description;
    public string IpAddresses => string.Join("\n", Value.Addresses.Select(a => $"{a.Address}/{a.PrefixLength}"));
    public string Gateways => string.Join(", ", Value.Gateways);
    public string Dns => string.Join(", ", Value.DnsServers);
    public string Mac => Value.MacAddress;
    public string Speed => $"{Value.LinkSpeed / 1_000_000} Mbps";
    public string Assignment => LocalizedText.Get(Value.Dhcp ? "settings.network.dhcp" : "settings.network.manual");
    public void Relocalize() => OnPropertyChanged(string.Empty);
}
