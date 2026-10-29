using System.Collections.ObjectModel;
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

public sealed partial class TailscaleViewModel : ObservableObject
{
    private readonly ITailscaleClient _client;
    private readonly ITailscaleOperationReferences _references;
    private readonly IHostElevationBroker _elevation;
    private readonly LocalizationService _language;
    private CancellationTokenSource? _lifetime;
    private HostSettingsConnection? _connection;
    private int _generation;
    private string _statusKey = "tailscale.loading";
    private TailscaleStatusDto? _status;
    private TailscaleControlSnapshot? _control;
    private TailscaleOperation? _operation;
    private Guid? _pendingReceipt;
    private Guid? _pendingClosure;
    private TailscaleRecoverySnapshot? _recovery;

    public TailscaleViewModel(ITailscaleClient client, ITailscaleOperationReferences references,
        IHostElevationBroker elevation, LocalizationService language)
    { _client = client; _references = references; _elevation = elevation; _language = language; }

    public Func<TailscaleOperation, TailscaleControlSnapshot, string, Task<bool>>? ConfirmAsync { get; set; }
    public Func<TailscaleRecoverySnapshot, string, Task<bool>>? ConfirmCloseAsync { get; set; }
    public Func<TailscaleConfiguration, Task<TailscaleConfiguration?>>? EditConfigurationAsync { get; set; }
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private TailscaleOperationItem? _selectedReference;
    public ObservableCollection<TailscalePeerItem> Peers { get; } = [];
    public ObservableCollection<TailscaleOperationItem> Operations { get; } = [];
    public string Target => _connection?.ServiceId ?? "";
    public string StatusText => Text(_statusKey);
    public string BackendState => _status?.BackendState is { } state
        ? _language.Get("tailscale.backend." + state, Text("tailscale.backend_unknown")) : "";
    public string Version => _status?.Version ?? "";
    public bool PeersRestricted => _status?.PeersRestricted == true;
    public bool HasControl => _control is not null;
    public string ConnectionText => _control is null ? Text("tailscale.control_not_loaded") : Text(_control.Connected ? "tailscale.connected" : "tailscale.disconnected");
    public string ConfigurationText => _control is null ? Text("tailscale.control_not_loaded")
        : string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("tailscale.configuration_summary"),
            Text(_control.Configuration.AcceptDns ? "tailscale.enabled" : "tailscale.disabled"),
            Text(_control.Configuration.AcceptRoutes ? "tailscale.enabled" : "tailscale.disabled"));
    public string ControlProblem => _control?.ProblemCode is { } code ? ProblemText(code) : "";
    public string OperationText => _pendingClosure is not null && _pendingClosure == SelectedReference?.Id ? Text("tailscale.recovery.awaiting_receipt")
        : _operation?.Closure is not null ? Text("tailscale.recovery.closed")
        : _pendingReceipt is not null && _pendingReceipt == SelectedReference?.Id ? Text("tailscale.awaiting_receipt")
        : _operation is null ? Text("tailscale.operation_empty") : Text("tailscale.operation_state." + _operation.State);
    public string OperationProblem => _operation?.Closure is not null ? "" : _operation?.ProblemCode is { } code ? ProblemText(code) : "";
    public string OperationUpdatedAt => _operation?.UpdatedAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "";
    public bool CanRefresh => !IsBusy && _lifetime is not null;
    public bool CanConnect => Editable && _control is { Connected: false, Authenticated: true };
    public bool CanDisconnect => Editable && _control is { Connected: true };
    public bool CanConfigure => Editable;
    public string RecoveryText => _recovery?.Observation is { } observation
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("tailscale.recovery.observation"),
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
    public void InvalidateControl() { _control = null; Notify(); }

    public async Task ActivateAsync()
    {
        if (_lifetime is not null) return;
        _lifetime = new();
        _client.SessionChanged += SessionChanged;
        _language.LanguageChanged += LanguageChanged;
        await RefreshAsync();
    }
    public void Deactivate()
    {
        _client.SessionChanged -= SessionChanged;
        _language.LanguageChanged -= LanguageChanged;
        _generation++;
        _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null;
        Clear(); IsBusy = false;
    }
    private void SessionChanged(object? sender, AuthSessionStateChangedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_lifetime is null || CurrentConnection) return;
        var active = _lifetime is not null;
        Deactivate();
        _statusKey = "tailscale.session_changed";
        if (active) { _lifetime = new(); _client.SessionChanged += SessionChanged; _language.LanguageChanged += LanguageChanged; }
        Notify();
    });
    private void LanguageChanged(object? sender, SystemLanguageChangedEventArgs e)
    {
        foreach (var peer in Peers) peer.RefreshLanguage();
        Notify();
    }
    private string Text(string key) => _language.Get(key, key);
    private string ProblemText(string key) => _language.Get(key, Text("tailscale.error"));
    private void Clear()
    {
        _connection = null; _status = null; _control = null; _operation = null; _pendingReceipt = null; _pendingClosure = null; _recovery = null;
        Peers.Clear(); Operations.Clear(); SelectedReference = null; Notify();
    }
    private bool Current(int generation, HostSettingsConnection connection, CancellationToken ct)
        => generation == _generation && !ct.IsCancellationRequested && _lifetime is not null && _client.IsCurrent(connection);
    partial void OnIsBusyChanged(bool value) => Notify();
    partial void OnSelectedReferenceChanged(TailscaleOperationItem? value) { _operation = null; _recovery = null; Notify(); }
    private void Notify()
    {
        foreach (var name in new[] { nameof(Target), nameof(StatusText), nameof(BackendState), nameof(Version), nameof(PeersRestricted),
            nameof(HasControl), nameof(ConnectionText), nameof(ConfigurationText), nameof(ControlProblem), nameof(OperationText),
            nameof(OperationProblem), nameof(OperationUpdatedAt), nameof(CanRefresh), nameof(CanConnect),
            nameof(CanDisconnect), nameof(CanConfigure), nameof(CanReopen), nameof(CanInspectRecovery), nameof(CanCloseRecovery), nameof(RecoveryText), nameof(RecoveryProblem) }) OnPropertyChanged(name);
        RefreshCommand.NotifyCanExecuteChanged(); LoadControlCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged(); DisconnectCommand.NotifyCanExecuteChanged(); ConfigureCommand.NotifyCanExecuteChanged();
        ReopenCommand.NotifyCanExecuteChanged(); InspectRecoveryCommand.NotifyCanExecuteChanged(); CloseRecoveryCommand.NotifyCanExecuteChanged();
    }
    private void Failure(Exception exception)
    {
        _statusKey = exception is RelaxKonOSAuthException auth && auth.Type.EndsWith("/elevation-required", StringComparison.Ordinal)
            ? "tailscale.authorization_required" : "tailscale.error";
        if (exception is RelaxKonOSAuthException problem && problem.Title.StartsWith("tailscale.", StringComparison.Ordinal))
            _statusKey = _language.Get(problem.Title, "") == "" ? "tailscale.error" : problem.Title;
        if (exception is InvalidOperationException { Message: "tailscale.operation_reference_failed" }) _statusKey = "tailscale.operation_reference_failed";
        Notify();
    }
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
        catch (Exception ex) when (ex is HttpRequestException or RelaxKonOSAuthException or InvalidOperationException
            or System.Text.Json.JsonException or IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
        { if (generation == _generation && !ct.IsCancellationRequested) Failure(ex); }
        finally { if (generation == _generation) { IsBusy = false; Notify(); } }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public Task RefreshAsync() => RunAsync(async (generation, connection, ct) =>
    {
        _statusKey = "tailscale.loading"; _status = null; _control = null; Peers.Clear(); Notify();
        var status = await _client.GetStatusAsync(connection, ct);
        if (!Current(generation, connection, ct)) return;
        _status = status;
        foreach (var peer in status.Self is null ? status.Peers ?? [] : new[] { status.Self }.Concat(status.Peers ?? [])) Peers.Add(new(peer, _language));
        var selected = SelectedReference?.Id;
        Operations.Clear();
        foreach (var id in _references.Read(connection)) Operations.Add(new(id));
        SelectedReference = Operations.FirstOrDefault(x => x.Id == selected) ?? Operations.FirstOrDefault();
        _statusKey = _language.Get("tailscale.state." + status.State, "") == "" ? "tailscale.error" : "tailscale.state." + status.State; Notify();
    });

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public Task LoadControlAsync() => RunAsync(async (generation, connection, ct) =>
    {
        _control = null; Notify();
        var control = await Elevated(() => _client.GetControlAsync(connection, ct), ct);
        if (!Current(generation, connection, ct)) return;
        _control = control; _statusKey = control.CanControl ? "tailscale.control_ready" : "tailscale.control_unavailable"; Notify();
    });
    private Task<T> Elevated<T>(Func<Task<T>> action, CancellationToken ct) => _elevation.ExecuteAsync(
        HostElevationCapability.HostNetworkChange, TailscaleAccess.Resource, action, ct);

    [RelayCommand(CanExecute = nameof(CanConnect))]
    public Task ConnectAsync() => ChangeAsync(TailscaleAction.Connect);
    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    public Task DisconnectAsync() => ChangeAsync(TailscaleAction.Disconnect);
    [RelayCommand(CanExecute = nameof(CanConfigure))]
    public Task ConfigureAsync() => ChangeAsync(TailscaleAction.Configure);
    private Task ChangeAsync(TailscaleAction action)
    {
        if (!(action switch { TailscaleAction.Connect => CanConnect, TailscaleAction.Disconnect => CanDisconnect, _ => CanConfigure })) return Task.CompletedTask;
        var baseline = _control!;
        var targetConnection = _connection;
        return RunAsync(async (generation, connection, ct) =>
        {
            if (connection != targetConnection || !Current(generation, connection, ct)) return;
            TailscaleConfiguration? config = null;
            if (action == TailscaleAction.Configure)
            {
                if (EditConfigurationAsync is null) return;
                config = await EditConfigurationAsync(baseline.Configuration);
                if (config is null || config == baseline.Configuration || !Current(generation, connection, ct)) return;
            }
            var request = new TailscalePrepareRequest(Guid.NewGuid(), baseline.Revision, action, config);
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
            _pendingReceipt = plan.OperationId; _control = null; _statusKey = "tailscale.awaiting_receipt"; Notify();
            var result = await Elevated(() => _client.ExecuteAsync(connection, plan.OperationId, ct), ct);
            if (!Current(generation, connection, ct)) return;
            Verify(result, request);
            _operation = result; if (result.State != TailscaleOperationState.Prepared) _pendingReceipt = null;
            _statusKey = "tailscale.operation_received"; Notify();
        });
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
                _operation = operation;
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
            _pendingClosure = id; _recovery = null; _control = null; _statusKey = "tailscale.recovery.awaiting_receipt"; Notify();
            var result = await Elevated(() => _client.CloseAsync(connection, request, ct), ct);
            if (!Current(generation, connection, ct) || SelectedReference?.Id != id) return;
            VerifyIdentity(result, id, connection);
            if (result.OperationId != id || result.State != TailscaleOperationState.Unknown || result.Closure is not { } closure
                || closure.OperationId != id || closure.Actor != connection.UserId.ToString("D") || closure.Observation?.Revision != request.ExpectedRevision)
                throw new InvalidDataException("tailscale.invalid_response");
            _operation = result; _pendingClosure = null; _statusKey = "tailscale.recovery.closed"; Notify();
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
            _operation = operation; _recovery = null;
            if (_pendingClosure == id && operation.Closure is not null) _pendingClosure = null;
            if (_pendingReceipt == id && operation.State != TailscaleOperationState.Prepared) _pendingReceipt = null;
            _statusKey = "tailscale.operation_received"; Notify();
        });
    }
}

public sealed record TailscaleOperationItem(Guid Id)
{
    public override string ToString() => Id.ToString("D");
}
public sealed class TailscalePeerItem(TailscalePeerDto peer, LocalizationService language) : ObservableObject
{
    public string HostName => peer.HostName;
    public string DnsName => peer.DnsName;
    public string Addresses => string.Join(", ", peer.Addresses);
    public string Status => language.Get(peer.Online ? "tailscale.online" : "tailscale.offline", "");
    internal void RefreshLanguage() => OnPropertyChanged(nameof(Status));
}
