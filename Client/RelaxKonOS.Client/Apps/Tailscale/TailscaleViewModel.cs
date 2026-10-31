using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.HostSettings;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tailscale;

namespace RelaxKonOS.Client.Apps.Tailscale;

/// <summary>
/// Live view of one host's Tailscale daemon. The read-only snapshot is loaded automatically when
/// the workspace opens (it is an observation, not a grant); every write still goes through the
/// unified <see cref="IHostElevationBroker"/> entry and an explicit risk confirmation.
/// </summary>
public sealed partial class TailscaleViewModel : ObservableObject
{
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ControlRefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ControlFreshness = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OperationFreshness = TimeSpan.FromSeconds(10);
    private readonly ITailscaleClient _client;
    private readonly ITailscaleOperationReferences _references;
    private readonly IHostElevationBroker _elevation;
    private readonly LocalizationService _language;
    private CancellationTokenSource? _lifetime;
    private HostSettingsConnection? _connection;
    private DispatcherTimer? _timer;
    private int _generation;
    private int _autoRefreshGate;
    private string _statusKey = "tailscale.loading";
    private TailscaleStatusDto? _status;
    private DateTimeOffset? _statusReadAt;
    private TailscaleControlSnapshot? _control;
    private DateTimeOffset? _controlReadAt;
    private bool _controlUnavailable;
    private string? _controlProblem;
    private TailscaleOperation? _operation;
    private Guid? _pendingReceipt;
    private Guid? _pendingClosure;
    private TailscaleRecoverySnapshot? _recovery;
    private Guid? _operationReadId;
    private DateTimeOffset? _operationReadAt;
    private string _visiblePage = "devices";
    private string _peerFilter = "";
    private bool _syncingPreferences;

    public TailscaleViewModel(ITailscaleClient client, ITailscaleOperationReferences references,
        IHostElevationBroker elevation, LocalizationService language)
    { _client = client; _references = references; _elevation = elevation; _language = language; }

    public Func<TailscaleOperation, TailscaleControlSnapshot, string, Task<bool>>? ConfirmAsync { get; set; }
    public Func<TailscaleRecoverySnapshot, string, Task<bool>>? ConfirmCloseAsync { get; set; }
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private TailscaleOperationItem? _selectedReference;
    [ObservableProperty] private bool _acceptDnsEnabled;
    [ObservableProperty] private bool _acceptRoutesEnabled;
    public ObservableCollection<TailscalePeerItem> Peers { get; } = [];
    public ObservableCollection<TailscalePeerItem> FilteredPeers { get; } = [];
    public ObservableCollection<TailscaleOperationItem> Operations { get; } = [];
    public string Target => _connection?.ServiceId ?? "";
    public string StatusText => Text(_statusKey);
    public string LastUpdatedText => _statusReadAt is { } at
        ? string.Format(CultureInfo.CurrentCulture, Text("tailscale.auto_updated"), at.ToLocalTime().ToString("T", CultureInfo.CurrentCulture))
        : Text("tailscale.never_updated");
    public bool HasStatus => _status is not null;
    public bool PeersRestricted => _status?.PeersRestricted == true;
    public string PeerSummary => string.Format(CultureInfo.CurrentCulture, Text("tailscale.peer_summary"), Peers.Count,
        Peers.Count(peer => peer.Online));
    public bool NoPeers => !PeersRestricted && Peers.Count == 0 && _status is { State: "observed" };
    public bool NoMatchingPeers => Peers.Count > 0 && FilteredPeers.Count == 0;
    public string PeerFilter
    {
        get => _peerFilter;
        set { if (_peerFilter == value) return; _peerFilter = value; OnPropertyChanged(); ApplyFilter(); Notify(); }
    }
    public string ConnectionBadge => _control is { } control ? Text(control.Connected ? "tailscale.connected" : "tailscale.disconnected")
        : _status is null ? Text("tailscale.loading")
        : _status.State != "observed" ? StateText(_status.State)
        : BackendText(_status.BackendState);
    public bool IsConnected => _control?.Connected ?? _status?.BackendState == "Running";
    public bool IsDisconnected => _control is { Connected: false } || _control is null && _status?.BackendState == "Stopped";
    public bool IsAttention => _status is not null && (_status.State != "observed"
        || _status.BackendState is "NeedsLogin" or "NeedsMachineAuth" or "Starting");
    public string ConnectionStateText => _control is { } control
        ? Text(control.Connected ? "tailscale.connected" : "tailscale.disconnected")
        : _status is { BackendState: { Length: > 0 } backend } ? BackendText(backend) : Text("tailscale.loading");
    public string ConnectionDetailText
    {
        get
        {
            var parts = new List<string>();
            if (_status?.Version is { Length: > 0 } version)
                parts.Add(string.Format(CultureInfo.CurrentCulture, Text("tailscale.version_format"), version));
            if (_status?.Self is { } self)
            {
                if (self.HostName.Length > 0)
                    parts.Add(string.Format(CultureInfo.CurrentCulture, Text("tailscale.self_format"), self.HostName));
                if (self.Addresses.Count > 0) parts.Add(string.Join(", ", self.Addresses));
            }
            if (parts.Count == 0 && _status is { State: { } state } && state != "observed") parts.Add(StateText(state));
            return string.Join(" · ", parts);
        }
    }
    public bool HasControl => _control is not null;
    public bool ControlUnavailable => _controlUnavailable;
    public bool HasControlProblem => _controlProblem is not null;
    public string ControlProblem => _controlProblem is { } code && _language.Get(code, "").Length > 0 ? Text(code) : "";
    public string ConnectionHint => _control is { } control
        ? !control.Authenticated ? Text("tailscale.login_required")
        : !control.CanControl ? (ControlProblem.Length > 0 ? ControlProblem : Text("tailscale.control_unavailable"))
        : ""
        : ControlProblem.Length > 0 ? ControlProblem : Text("tailscale.control_loading");
    public bool HasConnectionHint => ConnectionHint.Length > 0;
    public string PreferencesHint => _control is null ? Text("tailscale.preferences_unavailable")
        : !_control.CanControl ? Text("tailscale.control_unavailable") : Text("tailscale.preferences_hint");
    public bool CanRefresh => !IsBusy && _lifetime is not null;
    public bool CanConnect => Editable && _control is { Connected: false, Authenticated: true };
    public bool CanDisconnect => Editable && _control is { Connected: true };
    public bool CanEditPreferences => Editable && _control is not null;
    public bool CanAuthorizePeers => CanRefresh && PeersRestricted && CurrentConnection;
    public string OperationText => _pendingClosure is not null && _pendingClosure == SelectedReference?.Id ? Text("tailscale.recovery.awaiting_receipt")
        : _operation?.Closure is not null ? Text("tailscale.recovery.closed")
        : _pendingReceipt is not null && _pendingReceipt == SelectedReference?.Id ? Text("tailscale.awaiting_receipt")
        : _operation is null ? Text("tailscale.operation_empty") : Text("tailscale.operation_state." + _operation.State);
    public string OperationProblem => _operation?.Closure is not null ? "" : _operation?.ProblemCode is { } code ? ProblemText(code) : "";
    public string OperationUpdatedAt => _operation?.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";
    public string RecoveryText => _recovery?.Observation is { } observation
        ? string.Format(CultureInfo.CurrentCulture, Text("tailscale.recovery.observation"),
            Text(observation.Connected ? "tailscale.connected" : "tailscale.disconnected"),
            Text(observation.Configuration.AcceptDns ? "tailscale.enabled" : "tailscale.disabled"),
            Text(observation.Configuration.AcceptRoutes ? "tailscale.enabled" : "tailscale.disabled")) : "";
    public string RecoveryProblem => _recovery?.ProblemCode is { } code ? ProblemText(code) : "";
    public bool CanInspectRecovery => CanReopen && _operation is { State: TailscaleOperationState.Unknown, Closure: null }
        && _operation.OperationId == SelectedReference?.Id;
    public bool CanCloseRecovery => CanInspectRecovery && _recovery is { CanClose: true, Observation: not null, ExpiresAt: not null }
        && _recovery.OperationId == SelectedReference?.Id;
    public bool CanReopen => CanRefresh && SelectedReference is not null && CurrentConnection;
    private bool Editable => CanRefresh && _pendingReceipt is null && _pendingClosure is null && _control?.CanControl == true && CurrentConnection;
    private bool CurrentConnection => _connection is { } connection && _client.IsCurrent(connection);

    public void InvalidateControl()
    {
        _control = null; _controlReadAt = null; _controlProblem = null; _controlUnavailable = false;
        SyncPreferences(); Notify();
    }

    public async Task ActivateAsync()
    {
        if (_lifetime is not null) return;
        _lifetime = new();
        _client.SessionChanged += SessionChanged;
        _language.LanguageChanged += LanguageChanged;
        StartAutoRefresh();
        await RefreshAsync();
    }
    public void Deactivate()
    {
        _client.SessionChanged -= SessionChanged;
        _language.LanguageChanged -= LanguageChanged;
        StopAutoRefresh();
        _generation++;
        _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null;
        Clear(); IsBusy = false;
    }

    /// <summary>Called by the shell when the visible page changes; the control page loads itself.</summary>
    public void SetVisiblePage(string page)
    {
        _visiblePage = page;
        if (page == "control") _ = EnsureControlAsync();
        else if (page == "operations") _ = AutoReadOperationAsync();
    }

    private void StartAutoRefresh()
    {
        StopAutoRefresh();
        _timer = new DispatcherTimer { Interval = AutoRefreshInterval };
        _timer.Tick += async (_, _) => await AutoRefreshAsync();
        _timer.Start();
    }
    private void StopAutoRefresh()
    {
        _timer?.Stop();
        _timer = null;
    }
    private void SessionChanged(object? sender, AuthSessionStateChangedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_lifetime is null || CurrentConnection) return;
        var active = _lifetime is not null;
        Deactivate();
        _statusKey = "tailscale.session_changed";
        if (active)
        {
            _lifetime = new();
            _client.SessionChanged += SessionChanged;
            _language.LanguageChanged += LanguageChanged;
            StartAutoRefresh();
            _ = RefreshAsync();
        }
        Notify();
    });
    private void LanguageChanged(object? sender, SystemLanguageChangedEventArgs e)
    {
        foreach (var peer in Peers) peer.RefreshLanguage();
        Notify();
    }
    private string Text(string key) => _language.Get(key, key);
    private string StateText(string state)
    {
        var key = "tailscale.state." + state;
        return _language.Get(key, "").Length > 0 ? Text(key) : Text("tailscale.error");
    }
    private string BackendText(string? state) => state is { Length: > 0 } && _language.Get("tailscale.backend." + state, "").Length > 0
        ? Text("tailscale.backend." + state) : Text("tailscale.backend_unknown");
    private string ProblemText(string key) => _language.Get(key, "").Length > 0 ? Text(key) : Text("tailscale.error");
    private string ProblemOrGeneric(string? code) => code is not null && _language.Get(code, "").Length > 0 ? code : "tailscale.error";
    private void Clear()
    {
        _connection = null; _status = null; _statusReadAt = null; _control = null; _controlReadAt = null;
        _controlProblem = null; _controlUnavailable = false; _operation = null; _pendingReceipt = null; _pendingClosure = null; _recovery = null;
        _operationReadId = null; _operationReadAt = null;
        Peers.Clear(); FilteredPeers.Clear(); Operations.Clear(); SelectedReference = null;
        SyncPreferences(); Notify();
    }
    private bool Current(int generation, HostSettingsConnection connection, CancellationToken ct)
        => generation == _generation && !ct.IsCancellationRequested && _lifetime is not null && _client.IsCurrent(connection);
    partial void OnIsBusyChanged(bool value) => Notify();
    partial void OnSelectedReferenceChanged(TailscaleOperationItem? value)
    {
        _operation = null; _recovery = null; _operationReadId = null; _operationReadAt = null; Notify();
        if (_visiblePage == "operations") _ = AutoReadOperationAsync();
    }
    partial void OnAcceptDnsEnabledChanged(bool value) { if (!_syncingPreferences) _ = SetAcceptDnsAsync(value); }
    partial void OnAcceptRoutesEnabledChanged(bool value) { if (!_syncingPreferences) _ = SetAcceptRoutesAsync(value); }
    internal void SyncPreferences()
    {
        _syncingPreferences = true;
        try
        {
            AcceptDnsEnabled = _control?.Configuration.AcceptDns == true;
            AcceptRoutesEnabled = _control?.Configuration.AcceptRoutes == true;
        }
        finally { _syncingPreferences = false; }
    }
    private void Notify()
    {
        foreach (var name in new[] { nameof(Target), nameof(StatusText), nameof(LastUpdatedText), nameof(HasStatus), nameof(PeersRestricted),
            nameof(PeerSummary), nameof(NoPeers), nameof(NoMatchingPeers), nameof(ConnectionBadge), nameof(IsConnected), nameof(IsDisconnected),
            nameof(IsAttention), nameof(ConnectionStateText), nameof(ConnectionDetailText), nameof(HasControl), nameof(ControlUnavailable),
            nameof(HasControlProblem), nameof(ControlProblem), nameof(ConnectionHint), nameof(HasConnectionHint), nameof(PreferencesHint),
            nameof(CanRefresh), nameof(CanConnect), nameof(CanDisconnect), nameof(CanEditPreferences), nameof(CanAuthorizePeers),
            nameof(OperationText), nameof(OperationProblem), nameof(OperationUpdatedAt), nameof(CanReopen), nameof(CanInspectRecovery),
            nameof(CanCloseRecovery), nameof(RecoveryText), nameof(RecoveryProblem) })
            OnPropertyChanged(name);
        RefreshCommand.NotifyCanExecuteChanged(); LoadControlCommand.NotifyCanExecuteChanged(); AuthorizePeersCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged(); DisconnectCommand.NotifyCanExecuteChanged();
        ReopenCommand.NotifyCanExecuteChanged(); InspectRecoveryCommand.NotifyCanExecuteChanged(); CloseRecoveryCommand.NotifyCanExecuteChanged();
    }
    private void ApplyStatus(TailscaleStatusDto status)
    {
        _status = status;
        _statusReadAt = DateTimeOffset.UtcNow;
        Peers.Clear();
        if (status.Self is { } self) Peers.Add(new(self, _language, true));
        foreach (var peer in status.Peers ?? []) Peers.Add(new(peer, _language, false));
        ApplyFilter();
        Notify();
    }
    private void ApplyFilter()
    {
        var filter = _peerFilter.Trim();
        FilteredPeers.Clear();
        foreach (var peer in Peers)
            if (filter.Length == 0 || peer.Matches(filter)) FilteredPeers.Add(peer);
        OnPropertyChanged(nameof(NoMatchingPeers));
        OnPropertyChanged(nameof(NoPeers));
    }
    private void ApplyControl(TailscaleControlSnapshot control)
    {
        _control = control;
        _controlReadAt = DateTimeOffset.UtcNow;
        _controlUnavailable = false;
        _controlProblem = control.ProblemCode;
        SyncPreferences();
        Notify();
    }
    private static bool Handled(Exception exception) => exception is HttpRequestException or RelaxKonOSAuthException or InvalidOperationException
        or System.Text.Json.JsonException or IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException;
    private void Failure(Exception exception)
    {
        // A transport failure after submission keeps the operation unproven instead of
        // replacing it with a generic error; the receipt is the only source of truth then.
        if (_pendingReceipt is not null && _pendingReceipt == SelectedReference?.Id)
        { _statusKey = "tailscale.awaiting_receipt"; Notify(); return; }
        if (_pendingClosure is not null && _pendingClosure == SelectedReference?.Id)
        { _statusKey = "tailscale.recovery.awaiting_receipt"; Notify(); return; }
        var problem = ProblemCode(exception);
        var code = problem == "tailscale.elevation_required" ? "tailscale.authorization_required" : ProblemOrGeneric(problem);
        _statusKey = code;
        Notify();
    }
    private static string? ProblemCode(Exception exception) => exception switch
    {
        RelaxKonOSAuthException auth when auth.Title.StartsWith("tailscale.", StringComparison.Ordinal) => auth.Title,
        HttpRequestException error when error.Message.StartsWith("tailscale.", StringComparison.Ordinal) => error.Message,
        InvalidOperationException error when error.Message.StartsWith("tailscale.", StringComparison.Ordinal) => error.Message,
        InvalidDataException error when error.Message.StartsWith("tailscale.", StringComparison.Ordinal) => error.Message,
        System.Text.Json.JsonException or IOException or UnauthorizedAccessException or OperationCanceledException => "tailscale.error",
        _ => null,
    };
    private async Task RunAsync(Func<int, HostSettingsConnection, CancellationToken, Task> action)
    {
        if (!CanRefresh) return;
        var generation = _generation;
        var ct = _lifetime!.Token;
        IsBusy = true;
        try
        {
            var connection = _client.CaptureConnection();
            if (_connection != connection) { Clear(); _connection = connection; }
            await action(generation, connection, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (Handled(ex))
        { if (generation == _generation && !ct.IsCancellationRequested) Failure(ex); }
        finally { if (generation == _generation) { IsBusy = false; Notify(); } }
    }

    private async Task<bool> ReadStatusCoreAsync(int generation, HostSettingsConnection connection, CancellationToken ct, bool updateMessage)
    {
        var status = await _client.GetStatusAsync(connection, ct);
        if (!Current(generation, connection, ct)) return false;
        ApplyStatus(status);
        if (updateMessage) _statusKey = "tailscale.state." + status.State;
        return true;
    }
    private async Task<bool> ReadControlCoreAsync(int generation, HostSettingsConnection connection, CancellationToken ct)
    {
        try
        {
            var control = await _client.GetControlAsync(connection, ct);
            if (!Current(generation, connection, ct)) return false;
            ApplyControl(control);
            _controlUnavailable = false;
            return true;
        }
        catch (RelaxKonOSAuthException error) when (error.Status == 403 && error.Type.EndsWith("/elevation-required", StringComparison.Ordinal))
        { if (Current(generation, connection, ct)) { _controlProblem = "tailscale.elevation_required"; Notify(); } return false; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception ex) when (Handled(ex))
        {
            if (Current(generation, connection, ct))
            {
                _controlUnavailable = true;
                _controlProblem = ProblemOrGeneric(ProblemCode(ex));
                Notify();
            }
            return false;
        }
    }
    private async Task AutoRefreshAsync()
    {
        if (_lifetime is null || IsBusy || _pendingReceipt is not null || _pendingClosure is not null) return;
        if (Interlocked.CompareExchange(ref _autoRefreshGate, 1, 0) != 0) return;
        var generation = _generation; var token = _lifetime.Token;
        HostSettingsConnection? connection = null;
        try
        {
            connection = _client.CaptureConnection();
            if (_connection != connection) return;
            if (!await ReadStatusCoreAsync(generation, connection, token, updateMessage: false)) return;
            if (!Current(generation, connection, token)) return;
            if (_statusKey == "tailscale.auto_refresh_failed") _statusKey = "tailscale.state." + (_status?.State ?? "observed");
            if (_visiblePage == "control" && (_control is null || _controlReadAt is null
                || DateTimeOffset.UtcNow - _controlReadAt.Value > ControlRefreshInterval))
                await ReadControlCoreAsync(generation, connection, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (Handled(ex))
        {
            if (connection is not null && Current(generation, connection, token)) { _statusKey = "tailscale.auto_refresh_failed"; Notify(); }
        }
        finally { Interlocked.Exchange(ref _autoRefreshGate, 0); }
    }

    /// <summary>One background refresh cycle; the workspace timer drives this while the window is open.</summary>
    public Task AutoRefreshOnceAsync() => AutoRefreshAsync();

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public Task RefreshAsync() => RunAsync(async (generation, connection, ct) =>
    {
        if (!await ReadStatusCoreAsync(generation, connection, ct, updateMessage: true)) return;
        var selected = SelectedReference?.Id;
        Operations.Clear();
        foreach (var id in _references.Read(connection)) Operations.Add(new(id));
        SelectedReference = Operations.FirstOrDefault(item => item.Id == selected) ?? Operations.FirstOrDefault();
        if (_visiblePage == "control" || _control is not null || _controlUnavailable)
        {
            await ReadControlCoreAsync(generation, connection, ct);
            if (!Current(generation, connection, ct)) return;
            _statusKey = _control is not null ? "tailscale.control_ready" : _controlProblem ?? "tailscale.state." + (_status?.State ?? "observed");
        }
        Notify();
    });

    /// <summary>Loads the connection snapshot without prompting where possible; used by the page itself.</summary>
    public Task EnsureControlAsync()
    {
        if (!CanRefresh) return Task.CompletedTask;
        if (_control is not null && _controlReadAt is { } at && DateTimeOffset.UtcNow - at < ControlFreshness) return Task.CompletedTask;
        return RunAsync(async (generation, connection, ct) =>
        {
            if (await ReadControlCoreAsync(generation, connection, ct) && generation == _generation)
            {
                _controlUnavailable = false; _controlProblem = null; _statusKey = "tailscale.control_ready";
            }
            Notify();
        });
    }
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public Task LoadControlAsync() => RunAsync(async (generation, connection, ct) =>
    {
        var control = await Elevated(() => _client.GetControlAsync(connection, ct), ct);
        if (!Current(generation, connection, ct)) return;
        ApplyControl(control); _controlUnavailable = false;
        _statusKey = control.CanControl ? "tailscale.control_ready" : "tailscale.control_unavailable";
        Notify();
    });
    private Task<T> Elevated<T>(Func<Task<T>> action, CancellationToken ct) => _elevation.ExecuteAsync(
        HostElevationCapability.HostNetworkChange, TailscaleAccess.Resource, action, ct);

    [RelayCommand(CanExecute = nameof(CanConnect))]
    public Task ConnectAsync() => ChangeAsync(TailscaleAction.Connect);
    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    public Task DisconnectAsync() => ChangeAsync(TailscaleAction.Disconnect);
    public Task<bool> SetAcceptDnsAsync(bool value) => SetPreferenceAsync(value, null);
    public Task<bool> SetAcceptRoutesAsync(bool value) => SetPreferenceAsync(null, value);
    private async Task<bool> SetPreferenceAsync(bool? acceptDns, bool? acceptRoutes)
    {
        if (_control is null || !CanEditPreferences) { SyncPreferences(); return false; }
        var configuration = new TailscaleConfiguration(acceptDns ?? _control.Configuration.AcceptDns,
            acceptRoutes ?? _control.Configuration.AcceptRoutes);
        if (configuration == _control.Configuration) return true;
        var applied = await ChangeAsync(TailscaleAction.Configure, configuration);
        SyncPreferences();
        return applied;
    }
    private async Task<bool> ChangeAsync(TailscaleAction action, TailscaleConfiguration? configuration = null)
    {
        if (!(action switch
            {
                TailscaleAction.Connect => CanConnect,
                TailscaleAction.Disconnect => CanDisconnect,
                _ => CanEditPreferences
            })) return false;
        var baseline = _control!;
        var targetConnection = _connection;
        var applied = false;
        await RunAsync(async (generation, connection, ct) =>
        {
            if (connection != targetConnection || !Current(generation, connection, ct)) return;
            if (action == TailscaleAction.Configure && configuration is null) return;
            var request = new TailscalePrepareRequest(Guid.NewGuid(), baseline.Revision, action, configuration);
            var plan = await Elevated(() => _client.PrepareAsync(connection, request, ct), ct);
            if (!Current(generation, connection, ct)) return;
            Verify(plan, request);
            if (plan.State != TailscaleOperationState.Prepared || plan.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidDataException("tailscale.invalid_response");
            // This write must succeed before a mutation is submitted, so a lost response remains discoverable.
            try { _references.Remember(connection, plan.OperationId); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { throw new InvalidOperationException("tailscale.operation_reference_failed", ex); }
            var item = new TailscaleOperationItem(plan.OperationId); Operations.Insert(0, item); SelectedReference = item; _operation = plan; Notify();
            if (ConfirmAsync is null || !await ConfirmAsync(plan, baseline, connection.ServiceId) || !Current(generation, connection, ct)) return;
            if (plan.ExpiresAt <= DateTimeOffset.UtcNow) { _statusKey = "tailscale.plan_expired"; Notify(); return; }
            _pendingReceipt = plan.OperationId; _statusKey = "tailscale.awaiting_receipt"; Notify();
            var result = await Elevated(() => _client.ExecuteAsync(connection, plan.OperationId, ct), ct);
            if (!Current(generation, connection, ct)) return;
            Verify(result, request);
            _operation = result; if (result.State != TailscaleOperationState.Prepared) _pendingReceipt = null;
            _statusKey = result.State == TailscaleOperationState.Applied ? "tailscale.operation_applied" : "tailscale.operation_received";
            applied = result.State == TailscaleOperationState.Applied;
            Notify();
            if (!applied) return;
            // The daemon already changed: refresh the live snapshot instead of asking the user to re-read it.
            await ReadControlCoreAsync(generation, connection, ct);
            await ReadStatusCoreAsync(generation, connection, ct, updateMessage: false);
            Notify();
        });
        return applied;
    }
    private static void Verify(TailscaleOperation operation, TailscalePrepareRequest request)
    {
        if (operation.OperationId != request.OperationId || operation.Action != request.Action || operation.ExpectedRevision != request.ExpectedRevision
            || operation.Configuration != request.Configuration || !Enum.IsDefined(operation.State)) throw new InvalidDataException("tailscale.invalid_response");
    }
    private static void VerifyIdentity(TailscaleOperation operation, Guid id, HostSettingsConnection connection)
    {
        if (operation.OperationId != id || !Enum.IsDefined(operation.State) || operation.Closure is { } closure
            && (operation.State != TailscaleOperationState.Unknown || closure.OperationId != id || closure.Actor != connection.UserId.ToString("D")
                || closure.ClosedAt == default || closure.Observation is null || closure.Observation.Configuration is null)) throw new InvalidDataException("tailscale.invalid_response");
    }
    [RelayCommand(CanExecute = nameof(CanInspectRecovery))]
    public Task InspectRecoveryAsync()
    {
        if (!CanInspectRecovery) return Task.CompletedTask;
        var id = SelectedReference!.Id; var target = _connection;
        return RunAsync(async (generation, connection, ct) =>
        {
            if (connection != target || !Current(generation, connection, ct)) return;
            _recovery = null; Notify();
            var recovery = await Elevated(() => _client.ReadRecoveryAsync(connection, id, ct), ct);
            if (!Current(generation, connection, ct) || SelectedReference?.Id != id) return;
            if (recovery.OperationId != id || recovery.Observation is { Configuration: null } || recovery.CanClose && (recovery.Observation is null || recovery.ExpiresAt is null)
                || recovery.Closure is { } closure && (closure.OperationId != id || closure.Actor != connection.UserId.ToString("D")
                    || closure.ClosedAt == default || closure.Observation is null || closure.Observation.Configuration is null || recovery.CanClose))
                throw new InvalidDataException("tailscale.invalid_response");
            _recovery = recovery;
            if (recovery.Closure is not null)
            {
                var operation = await _client.GetOperationAsync(connection, id, ct);
                if (!Current(generation, connection, ct) || SelectedReference?.Id != id) return;
                VerifyIdentity(operation, id, connection);
                _operation = operation; RememberOperationRead(id);
                if (operation.Closure is not null && _pendingClosure == id) _pendingClosure = null;
            }
            _statusKey = "tailscale.recovery.observed"; Notify();
        });
    }
    [RelayCommand(CanExecute = nameof(CanCloseRecovery))]
    public Task CloseRecoveryAsync()
    {
        if (!CanCloseRecovery) return Task.CompletedTask;
        var preview = _recovery!; var id = preview.OperationId; var target = _connection;
        return RunAsync(async (generation, connection, ct) =>
        {
            if (connection != target || !Current(generation, connection, ct)) return;
            if (preview.ExpiresAt <= DateTimeOffset.UtcNow) { _recovery = null; _statusKey = "tailscale.plan_expired"; Notify(); return; }
            if (ConfirmCloseAsync is null || !await ConfirmCloseAsync(preview, connection.ServiceId)
                || !Current(generation, connection, ct) || SelectedReference?.Id != id) return;
            if (preview.ExpiresAt <= DateTimeOffset.UtcNow) { _recovery = null; _statusKey = "tailscale.plan_expired"; Notify(); return; }
            var request = new TailscaleCloseRequest(id, preview.Observation!.Revision, true);
            _pendingClosure = id; _recovery = null; _statusKey = "tailscale.recovery.awaiting_receipt"; Notify();
            var result = await Elevated(() => _client.CloseAsync(connection, request, ct), ct);
            if (!Current(generation, connection, ct) || SelectedReference?.Id != id) return;
            VerifyIdentity(result, id, connection);
            if (result.OperationId != id || result.State != TailscaleOperationState.Unknown || result.Closure is not { } closure
                || closure.OperationId != id || closure.Actor != connection.UserId.ToString("D") || closure.Observation?.Revision != request.ExpectedRevision)
                throw new InvalidDataException("tailscale.invalid_response");
            _operation = result; _pendingClosure = null; _statusKey = "tailscale.recovery.closed"; Notify();
            await ReadControlCoreAsync(generation, connection, ct);
        });
    }

    [RelayCommand(CanExecute = nameof(CanReopen))]
    public Task ReopenAsync()
    {
        if (!CanReopen) return Task.CompletedTask;
        var id = SelectedReference!.Id;
        var targetConnection = _connection;
        return RunAsync(async (generation, connection, ct) =>
        {
            if (connection != targetConnection || !Current(generation, connection, ct)) return;
            var operation = await _client.GetOperationAsync(connection, id, ct);
            if (!Current(generation, connection, ct) || SelectedReference?.Id != id) return;
            VerifyIdentity(operation, id, connection);
            _operation = operation; _recovery = null; RememberOperationRead(id);
            if (_pendingClosure == id && operation.Closure is not null) _pendingClosure = null;
            if (_pendingReceipt == id && operation.State != TailscaleOperationState.Prepared) _pendingReceipt = null;
            _statusKey = "tailscale.operation_received"; Notify();
        });
    }
    private void RememberOperationRead(Guid id) { _operationReadId = id; _operationReadAt = DateTimeOffset.UtcNow; }
    private Task AutoReadOperationAsync()
    {
        if (!CanReopen || SelectedReference is not { } item) return Task.CompletedTask;
        if (_operationReadId == item.Id && _operationReadAt is { } at && DateTimeOffset.UtcNow - at < OperationFreshness) return Task.CompletedTask;
        return ReopenAsync();
    }

    [RelayCommand(CanExecute = nameof(CanAuthorizePeers))]
    public Task AuthorizePeersAsync() => RunAsync(async (generation, connection, ct) =>
    {
        var granted = await _elevation.RequestGrantAsync(HostElevationCapability.HostNetworkChange, TailscaleAccess.Resource, ct);
        if (!granted) { if (Current(generation, connection, ct)) { _statusKey = "tailscale.authorization_required"; Notify(); } return; }
        if (!Current(generation, connection, ct)) return;
        await ReadStatusCoreAsync(generation, connection, ct, updateMessage: false);
        _statusKey = "tailscale.authorization_granted"; Notify();
    });

    /// <summary>Latest backend state for the login page without taking a second connection snapshot.</summary>
    public async Task<string?> ReadBackendStateAsync(CancellationToken ct)
    {
        if (_status is not null && _statusReadAt is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromSeconds(4)) return _status.BackendState;
        if (_lifetime is null || _connection is not { } connection) return _status?.BackendState;
        try
        {
            var status = await _client.GetStatusAsync(connection, ct);
            return status.State == "observed" ? status.BackendState : null;
        }
        catch (Exception ex) when (Handled(ex)) { return null; }
    }
}

public sealed record TailscaleOperationItem(Guid Id)
{
    public override string ToString() => Id.ToString("D");
}
public sealed class TailscalePeerItem(TailscalePeerDto peer, LocalizationService language, bool self) : ObservableObject
{
    public string HostName => peer.HostName;
    public string DnsName => peer.DnsName;
    public string Addresses => string.Join(", ", peer.Addresses);
    public string Status => language.Get(peer.Online ? "tailscale.online" : "tailscale.offline", "");
    public string Role => language.Get(self ? "tailscale.role_self" : peer.ExitNode ? "tailscale.role_exit_node" : "tailscale.role_peer", "");
    public bool Online => peer.Online;
    internal bool IsSelf => self;
    internal bool Matches(string filter) => peer.HostName.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || peer.DnsName.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || peer.Addresses.Any(address => address.Contains(filter, StringComparison.OrdinalIgnoreCase));
    internal void RefreshLanguage() { OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Role)); }
}
