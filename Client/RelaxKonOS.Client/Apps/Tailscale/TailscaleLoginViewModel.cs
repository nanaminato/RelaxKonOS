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

/// <summary>Holds operation identities, never a browser challenge URL. Reopening only reads original evidence.</summary>
public sealed partial class TailscaleLoginViewModel(ITailscaleLoginClient client, ITailscaleOperationReferences references,
    IHostElevationBroker elevation, LocalizationService language) : ObservableObject
{
    private CancellationTokenSource? _lifetime;
    private HostSettingsConnection? _connection;
    private int _generation;
    private TailscaleLoginOperation? _operation;
    private TailscaleLoginRecoverySnapshot? _recovery;
    private Guid? _pending;
    private bool _closingPending;
    private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private TailscaleOperationItem? _selected;
    public ObservableCollection<TailscaleOperationItem> Operations { get; } = [];
    public Func<string, Guid, bool, Task<bool>>? ConfirmAsync { get; set; }
    public Func<Uri, Task<bool>>? OpenBrowserAsync { get; set; }
    public Action? HostMayHaveChanged { get; set; }
    public string Target => _connection?.ServiceId ?? "";
    public string Message => _message.Length == 0 ? "" : language.Get(_message, language.Get("tailscale.error", ""));
    public string Result => _pending == Selected?.Id && _pending is not null ? language.Get("tailscale.awaiting_receipt", "")
        : _operation?.Closure is not null ? language.Get("tailscale.login.closed", "")
        : _operation is null ? "" : language.Get("tailscale.login.state." + _operation.State, "");
    public string Observation => _recovery?.Observation is { } observation
        ? language.Get("tailscale.backend." + observation.BackendState, language.Get("tailscale.backend_unknown", "")) : "";
    public string ObservedBackendState => _recovery?.Observation?.BackendState ?? "";
    public bool Ready => _lifetime is not null && !IsBusy && _connection is { } connection && client.IsCurrent(connection);
    public bool CanStart => Ready && _pending is null && (_operation is null || _operation.Closure is not null
        || _operation.State is TailscaleLoginState.Prepared or TailscaleLoginState.Completed or TailscaleLoginState.Rejected);
    public bool CanRead => Ready && Selected is not null;
    public bool CanOpen => CanRead && _pending is null && _operation is { State: TailscaleLoginState.Issued, Closure: null }
        && _operation.OperationId == Selected?.Id && _operation.ExpiresAt > DateTimeOffset.UtcNow;
    public bool CanInspect => CanRead && _operation is { Closure: null, State: TailscaleLoginState.Unknown or TailscaleLoginState.Issued or TailscaleLoginState.ApprovalRequired }
        && _operation.OperationId == Selected?.Id;
    public bool CanClose => CanInspect && _recovery is { CanClose: true, Observation: not null, ExpiresAt: not null }
        && _recovery.OperationId == Selected?.Id && _recovery.ExpiresAt > DateTimeOffset.UtcNow;

    public void Activate()
    {
        if (_lifetime is not null) return;
        _lifetime = new(); client.SessionChanged += SessionChanged; language.LanguageChanged += LanguageChanged;
        try { _connection = client.CaptureConnection(); Reload(); }
        catch (RelaxKonOSAuthException) { _message = "tailscale.session_changed"; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { _connection = null; _message = "tailscale.operation_reference_failed"; }
        Notify();
    }
    public void Deactivate()
    {
        client.SessionChanged -= SessionChanged; language.LanguageChanged -= LanguageChanged;
        _generation++; _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null;
        _connection = null; _operation = null; _recovery = null; _pending = null; Selected = null; Operations.Clear(); IsBusy = false; Notify();
    }
    private void SessionChanged(object? sender, AuthSessionStateChangedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_lifetime is null || _connection is { } connection && client.IsCurrent(connection)) return;
        Deactivate(); Activate(); _message = "tailscale.session_changed"; Notify();
    });
    private void LanguageChanged(object? sender, SystemLanguageChangedEventArgs e) => Notify();
    private void Reload()
    {
        Operations.Clear();
        foreach (var id in references.Read(_connection!)) Operations.Add(new(id));
        Selected = Operations.FirstOrDefault();
    }
    partial void OnSelectedChanged(TailscaleOperationItem? value) { _operation = null; _recovery = null; Notify(); }
    partial void OnIsBusyChanged(bool value) => Notify();
    private void Notify()
    {
        foreach (var name in new[] { nameof(Target), nameof(Message), nameof(Result), nameof(Observation), nameof(Ready), nameof(CanStart), nameof(CanRead), nameof(CanOpen), nameof(CanInspect), nameof(CanClose) }) OnPropertyChanged(name);
        StartCommand.NotifyCanExecuteChanged(); ReadCommand.NotifyCanExecuteChanged(); OpenCommand.NotifyCanExecuteChanged();
        InspectCommand.NotifyCanExecuteChanged(); CloseCommand.NotifyCanExecuteChanged();
    }
    private bool Current(int generation, HostSettingsConnection connection, CancellationToken ct)
        => generation == _generation && _lifetime is not null && !ct.IsCancellationRequested && client.IsCurrent(connection);
    private Task<T> Elevated<T>(Func<Task<T>> action, CancellationToken ct) => elevation.ExecuteAsync(HostElevationCapability.HostNetworkChange, TailscaleAccess.Resource, action, ct);
    private async Task Run(Func<int, HostSettingsConnection, CancellationToken, Task> action)
    {
        if (!Ready) return;
        var generation = _generation; var connection = _connection!; var ct = _lifetime!.Token;
        IsBusy = true;
        try { await action(generation, connection, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or RelaxKonOSAuthException or InvalidOperationException
            or UnauthorizedAccessException or System.Text.Json.JsonException or OperationCanceledException)
        { if (Current(generation, connection, ct)) _message = "tailscale.error"; }
        finally { if (generation == _generation) { IsBusy = false; Notify(); } }
    }
    private static void Verify(TailscaleLoginOperation operation, Guid id, HostSettingsConnection connection)
    {
        if (operation.OperationId != id || !Enum.IsDefined(operation.State) || !Revision(operation.ExpectedRevision)
            || operation.ExpiresAt == default || operation.UpdatedAt == default
            || operation.Closure is { } closed && (closed.Intent is null || closed.Observation is null
                || operation.State is not (TailscaleLoginState.Issued or TailscaleLoginState.Unknown or TailscaleLoginState.ApprovalRequired)
                || closed.Intent.OperationId != id || closed.Intent.Actor != connection.UserId
                || closed.Intent.SessionId == Guid.Empty || closed.Intent.ExpectedRevision != operation.ExpectedRevision
                || closed.Intent.ExpiresAt != operation.ExpiresAt || closed.ClosedAt == default || !Revision(closed.Observation.Revision)
                || !Revision(closed.Observation.PreferencesRevision)))
            throw new InvalidDataException("tailscale.invalid_response");
    }
    private static bool Revision(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    [RelayCommand(CanExecute = nameof(CanStart))]
    public Task StartAsync() => Run(async (generation, connection, ct) =>
    {
        var id = Guid.NewGuid();
        var plan = await Elevated(() => client.PrepareAsync(connection, id, ct), ct);
        if (!Current(generation, connection, ct)) return;
        Verify(plan, id, connection);
        if (plan.State != TailscaleLoginState.Prepared || plan.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidDataException("tailscale.invalid_response");
        references.Remember(connection, id);
        var item = new TailscaleOperationItem(id); Operations.Insert(0, item); Selected = item; _operation = plan; Notify();
        if (ConfirmAsync is null || !await ConfirmAsync(connection.ServiceId, id, false) || !Current(generation, connection, ct)) return;
        if (plan.ExpiresAt <= DateTimeOffset.UtcNow) { _message = "tailscale.plan_expired"; return; }
        _pending = id; _closingPending = false; _recovery = null; HostMayHaveChanged?.Invoke(); Notify();
        var result = await Elevated(() => client.ExecuteAsync(connection, id, ct), ct);
        if (!Current(generation, connection, ct)) return;
        Verify(result, id, connection);
        if (result.ExpectedRevision != plan.ExpectedRevision || result.ExpiresAt != plan.ExpiresAt) throw new InvalidDataException("tailscale.invalid_response");
        _operation = result; if (result.State != TailscaleLoginState.Prepared) _pending = null; _message = "tailscale.operation_received";
    });
    [RelayCommand(CanExecute = nameof(CanRead))]
    public Task ReadAsync()
    {
        if (!CanRead) return Task.CompletedTask;
        var id = Selected!.Id;
        return Run(async (generation, connection, ct) =>
        {
            var result = await client.GetAsync(connection, id, ct);
            if (!Current(generation, connection, ct) || Selected?.Id != id) return;
            Verify(result, id, connection); _operation = result; _recovery = null;
            if (_pending == id && result.State != TailscaleLoginState.Prepared && (!_closingPending || result.Closure is not null)) _pending = null;
            _message = "tailscale.operation_received";
        });
    }
    [RelayCommand(CanExecute = nameof(CanOpen))]
    public Task OpenAsync()
    {
        if (!CanOpen || OpenBrowserAsync is null) return Task.CompletedTask;
        var operation = _operation!;
        return Run(async (generation, connection, ct) =>
        {
            var challenge = await Elevated(() => client.ChallengeAsync(connection, operation.OperationId, ct), ct);
            if (!Current(generation, connection, ct) || Selected?.Id != operation.OperationId) return;
            if (challenge.OperationId != operation.OperationId || challenge.ExpiresAt != operation.ExpiresAt
                || challenge.ExpiresAt <= DateTimeOffset.UtcNow || !TailscaleLoginUrl.IsOfficial(challenge.AuthenticationUrl))
                throw new InvalidDataException("tailscale.invalid_response");
            // The URL exists only for this explicit handoff; no property, clipboard or durable reference holds it.
            var opened = await OpenBrowserAsync(new Uri(challenge.AuthenticationUrl));
            if (Current(generation, connection, ct)) _message = opened ? "tailscale.login.browser_opened" : "tailscale.login.browser_failed";
        });
    }
    [RelayCommand(CanExecute = nameof(CanInspect))]
    public Task InspectAsync()
    {
        if (!CanInspect) return Task.CompletedTask;
        var id = Selected!.Id;
        return Run(async (generation, connection, ct) =>
        {
            _recovery = null;
            var preview = await Elevated(() => client.RecoveryAsync(connection, id, ct), ct);
            if (!Current(generation, connection, ct) || Selected?.Id != id) return;
            if (preview.OperationId != id || preview.CanClose && (preview.Observation is null || !Revision(preview.Observation.Revision)
                || preview.ExpiresAt is null || preview.ExpiresAt <= DateTimeOffset.UtcNow)) throw new InvalidDataException("tailscale.invalid_response");
            _recovery = preview; _message = "tailscale.recovery.observed";
            if (preview.Closure is not null)
            {
                var result = await client.GetAsync(connection, id, ct);
                if (!Current(generation, connection, ct) || Selected?.Id != id) return;
                Verify(result, id, connection); _operation = result;
                if (result.Closure is not null && _pending == id) _pending = null;
            }
        });
    }
    [RelayCommand(CanExecute = nameof(CanClose))]
    public Task CloseAsync()
    {
        if (!CanClose) return Task.CompletedTask;
        var preview = _recovery!;
        return Run(async (generation, connection, ct) =>
        {
            if (ConfirmAsync is null || !await ConfirmAsync(connection.ServiceId, preview.OperationId, true)
                || !Current(generation, connection, ct) || Selected?.Id != preview.OperationId) return;
            if (preview.ExpiresAt <= DateTimeOffset.UtcNow) { _recovery = null; _message = "tailscale.plan_expired"; return; }
            _pending = preview.OperationId; _closingPending = true; _recovery = null; HostMayHaveChanged?.Invoke(); Notify();
            var result = await Elevated(() => client.CloseAsync(connection, new(preview.OperationId, preview.Observation!.Revision, true), ct), ct);
            if (!Current(generation, connection, ct) || Selected?.Id != preview.OperationId) return;
            Verify(result, preview.OperationId, connection);
            if (result.Closure?.Observation.Revision != preview.Observation!.Revision) throw new InvalidDataException("tailscale.invalid_response");
            _operation = result; _pending = null; _message = "tailscale.login.closed";
        });
    }
}
