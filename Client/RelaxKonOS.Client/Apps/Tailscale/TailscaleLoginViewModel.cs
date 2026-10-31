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

/// <summary>
/// Holds operation identities, never a browser challenge URL. Reopening only reads original
/// evidence. The page observes the host state on its own so an already-signed-in host is
/// explained instead of failing a login attempt with a generic error.
/// </summary>
public sealed partial class TailscaleLoginViewModel(ITailscaleLoginClient client, ITailscaleOperationReferences references,
    IHostElevationBroker elevation, LocalizationService language) : ObservableObject
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReadFreshness = TimeSpan.FromSeconds(10);
    private CancellationTokenSource? _lifetime;
    private HostSettingsConnection? _connection;
    private int _generation;
    private TailscaleLoginOperation? _operation;
    private TailscaleLoginRecoverySnapshot? _recovery;
    private Guid? _pending;
    private bool _closingPending;
    private string _message = "";
    private string? _backendState;
    private DispatcherTimer? _poll;
    private int _pollGate;
    private bool _visible;
    private bool _completionDetected;
    private bool _completionRead;
    private Guid? _readId;
    private DateTimeOffset? _readAt;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private TailscaleOperationItem? _selected;
    private static readonly HashSet<string> InformationalMessages = new(StringComparer.Ordinal)
    {
        "tailscale.operation_received", "tailscale.recovery.observed", "tailscale.login.closed",
        "tailscale.login.browser_opened", "tailscale.login.completed_detected", "tailscale.authorization_granted",
        "tailscale.control_ready", "tailscale.state.observed",
    };
    public ObservableCollection<TailscaleOperationItem> Operations { get; } = [];
    public Func<string, Guid, bool, Task<bool>>? ConfirmAsync { get; set; }
    public Func<Uri, Task<bool>>? OpenBrowserAsync { get; set; }
    public Func<CancellationToken, Task<string?>>? ReadBackendStateAsync { get; set; }
    public Action? HostMayHaveChanged { get; set; }
    public string Target => _connection?.ServiceId ?? "";
    public string Message => _message.Length == 0 ? "" : ProblemText(_message);
    public bool MessageIsError => _message.Length > 0 && !InformationalMessages.Contains(_message);
    public bool MessageIsInfo => _message.Length > 0 && InformationalMessages.Contains(_message);
    public string Result => _pending is not null && _pending == Selected?.Id ? language.Get("tailscale.awaiting_receipt", "")
        : _operation?.Closure is not null ? language.Get("tailscale.login.closed", "")
        : _operation is null ? "" : language.Get("tailscale.login.state." + _operation.State, "");
    public string Observation => _recovery?.Observation is { } observation
        ? language.Get("tailscale.backend." + observation.BackendState, language.Get("tailscale.backend_unknown", "")) : "";
    public string ObservedBackendState => _recovery?.Observation?.BackendState ?? "";
    public string AvailabilityText => _backendState switch
    {
        "NeedsLogin" => language.Get("tailscale.login.need_login", ""),
        "NeedsMachineAuth" => language.Get("tailscale.login.device_approval", ""),
        "Running" => language.Get("tailscale.login.already_connected", ""),
        "Stopped" => language.Get("tailscale.login.already_stopped", ""),
        "Starting" => language.Get("tailscale.login.starting", ""),
        _ => language.Get("tailscale.login.status_unknown", ""),
    };
    public bool HasOperations => Operations.Count > 0;
    public bool NoOperations => Operations.Count == 0;
    public bool CompletionDetected => _completionDetected;
    public bool Ready => _lifetime is not null && !IsBusy && _connection is { } connection && client.IsCurrent(connection);
    public bool CanStart => Ready && _pending is null && !AlreadyLoggedIn && (_operation is null || _operation.Closure is not null
        || _operation.State is TailscaleLoginState.Prepared or TailscaleLoginState.Completed or TailscaleLoginState.Rejected);
    public bool CanRead => Ready && Selected is not null;
    public bool CanOpen => CanRead && _pending is null && _operation is { State: TailscaleLoginState.Issued, Closure: null }
        && _operation.OperationId == Selected?.Id && _operation.ExpiresAt > DateTimeOffset.UtcNow;
    public bool CanInspect => CanRead && _operation is { Closure: null, State: TailscaleLoginState.Unknown or TailscaleLoginState.Issued or TailscaleLoginState.ApprovalRequired }
        && _operation.OperationId == Selected?.Id;
    public bool CanClose => CanInspect && _recovery is { CanClose: true, Observation: not null, ExpiresAt: not null }
        && _recovery.OperationId == Selected?.Id && _recovery.ExpiresAt > DateTimeOffset.UtcNow;
    public bool CanFinish => CanInspect;
    private bool AlreadyLoggedIn => _backendState is "Running" or "Stopped" or "NeedsMachineAuth";

    public void Activate()
    {
        if (_lifetime is not null) return;
        _lifetime = new(); client.SessionChanged += SessionChanged; language.LanguageChanged += LanguageChanged;
        try { _connection = client.CaptureConnection(); Reload(); }
        catch (RelaxKonOSAuthException) { _message = "tailscale.session_changed"; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { _connection = null; _message = "tailscale.operation_reference_failed"; }
        Notify();
        if (_connection is not null) { _ = ObserveAndSettleAsync(); StartPollingIfNeeded(); }
    }
    public void Deactivate()
    {
        client.SessionChanged -= SessionChanged; language.LanguageChanged -= LanguageChanged;
        StopPolling();
        _generation++; _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null;
        _connection = null; _operation = null; _recovery = null; _pending = null; Selected = null; Operations.Clear();
        _backendState = null; _completionDetected = false; _completionRead = false; _readId = null; _readAt = null;
        IsBusy = false; Notify();
    }
    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (visible) { _ = ObserveAndSettleAsync(); _ = AutoReadAsync(); StartPollingIfNeeded(); }
        else StopPolling();
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
    partial void OnSelectedChanged(TailscaleOperationItem? value)
    {
        _operation = null; _recovery = null; _readId = null; _readAt = null; Notify();
        if (_visible) _ = AutoReadAsync();
    }
    partial void OnIsBusyChanged(bool value) => Notify();
    private void Notify()
    {
        foreach (var name in new[] { nameof(Target), nameof(Message), nameof(MessageIsError), nameof(MessageIsInfo), nameof(Result),
            nameof(Observation), nameof(AvailabilityText), nameof(HasOperations), nameof(NoOperations), nameof(CompletionDetected),
            nameof(Ready), nameof(CanStart), nameof(CanRead), nameof(CanOpen), nameof(CanInspect), nameof(CanClose), nameof(CanFinish) }) OnPropertyChanged(name);
        StartCommand.NotifyCanExecuteChanged(); ReadCommand.NotifyCanExecuteChanged(); OpenCommand.NotifyCanExecuteChanged();
        InspectCommand.NotifyCanExecuteChanged(); CloseCommand.NotifyCanExecuteChanged(); FinishCommand.NotifyCanExecuteChanged();
    }
    private bool Current(int generation, HostSettingsConnection connection, CancellationToken ct)
        => generation == _generation && _lifetime is not null && !ct.IsCancellationRequested && client.IsCurrent(connection);
    private Task<T> Elevated<T>(Func<Task<T>> action, CancellationToken ct) => elevation.ExecuteAsync(HostElevationCapability.HostNetworkChange, TailscaleAccess.Resource, action, ct);
    private static bool Handled(Exception exception) => exception is IOException or InvalidDataException or HttpRequestException or RelaxKonOSAuthException
        or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException or OperationCanceledException;
    private string ProblemText(string code) => language.Get(code, "").Length > 0 ? language.Get(code, "") : language.Get("tailscale.error", "");
    private static string? ProblemCode(Exception exception) => exception switch
    {
        RelaxKonOSAuthException auth when auth.Title.StartsWith("tailscale.", StringComparison.Ordinal) => auth.Title,
        HttpRequestException error when error.Message.StartsWith("tailscale.", StringComparison.Ordinal) => error.Message,
        InvalidOperationException error when error.Message.StartsWith("tailscale.", StringComparison.Ordinal) => error.Message,
        InvalidDataException error when error.Message.StartsWith("tailscale.", StringComparison.Ordinal) => error.Message,
        _ => null,
    };
    private async Task Run(Func<int, HostSettingsConnection, CancellationToken, Task> action)
    {
        if (!Ready) return;
        var generation = _generation; var connection = _connection!; var ct = _lifetime!.Token;
        IsBusy = true;
        try { await action(generation, connection, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (Handled(ex))
        {
            if (Current(generation, connection, ct))
                _message = ProblemCode(ex) is { } code && language.Get(code, "").Length > 0 ? code : "tailscale.error";
        }
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
    public Task StartAsync()
    {
        if (!CanStart) return Task.CompletedTask;
        return Run(async (generation, connection, ct) =>
        {
            _message = ""; _completionDetected = false; _completionRead = false; Notify();
            var id = Guid.NewGuid();
            var plan = await Elevated(() => client.PrepareAsync(connection, id, ct), ct);
            if (!Current(generation, connection, ct)) return;
            Verify(plan, id, connection);
            if (plan.State != TailscaleLoginState.Prepared || plan.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidDataException("tailscale.invalid_response");
            // A lost response is only recoverable through this durable reference, so a failed
            // save stops the login before any native action is submitted.
            try { references.Remember(connection, id); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { throw new InvalidOperationException("tailscale.operation_reference_failed", ex); }
            var item = new TailscaleOperationItem(id); Operations.Insert(0, item); Selected = item; _operation = plan; RememberRead(id); Notify();
            if (ConfirmAsync is null || !await ConfirmAsync(connection.ServiceId, id, false) || !Current(generation, connection, ct)) return;
            if (plan.ExpiresAt <= DateTimeOffset.UtcNow) { _message = "tailscale.plan_expired"; Notify(); return; }
            _pending = id; _closingPending = false; _recovery = null; HostMayHaveChanged?.Invoke(); Notify();
            var result = await Elevated(() => client.ExecuteAsync(connection, id, ct), ct);
            if (!Current(generation, connection, ct)) return;
            Verify(result, id, connection);
            if (result.ExpectedRevision != plan.ExpectedRevision || result.ExpiresAt != plan.ExpiresAt) throw new InvalidDataException("tailscale.invalid_response");
            _operation = result; if (result.State != TailscaleLoginState.Prepared) _pending = null;
            RememberRead(id);
            _message = "tailscale.operation_received"; Notify();
            // The confirmation already explained that the official page opens on this device;
            // delivering the challenge now is the same explicit action, not a second implicit one.
            if (result.State == TailscaleLoginState.Issued) await OpenBrowserCoreAsync(generation, connection, ct);
            else if (result.State is TailscaleLoginState.Completed or TailscaleLoginState.ApprovalRequired) _completionDetected = true;
            StartPollingIfNeeded();
            Notify();
        });
    }
    [RelayCommand(CanExecute = nameof(CanRead))]
    public Task ReadAsync()
    {
        if (!CanRead) return Task.CompletedTask;
        var id = Selected!.Id;
        return Run(async (generation, connection, ct) =>
        {
            var result = await client.GetAsync(connection, id, ct);
            if (!Current(generation, connection, ct) || Selected?.Id != id) return;
            Verify(result, id, connection); _operation = result; _recovery = null; RememberRead(id);
            if (_pending == id && result.State != TailscaleLoginState.Prepared && (!_closingPending || result.Closure is not null)) _pending = null;
            if (result.Closure is not null) { _completionDetected = false; StopPolling(); }
            _message = "tailscale.operation_received"; StartPollingIfNeeded(); Notify();
        });
    }
    [RelayCommand(CanExecute = nameof(CanOpen))]
    public Task OpenAsync()
    {
        if (!CanOpen || OpenBrowserAsync is null) return Task.CompletedTask;
        return Run(async (generation, connection, ct) => await OpenBrowserCoreAsync(generation, connection, ct));
    }
    private async Task OpenBrowserCoreAsync(int generation, HostSettingsConnection connection, CancellationToken ct)
    {
        if (OpenBrowserAsync is null || _operation is not { } operation) return;
        var challenge = await Elevated(() => client.ChallengeAsync(connection, operation.OperationId, ct), ct);
        if (!Current(generation, connection, ct) || Selected?.Id != operation.OperationId) return;
        if (challenge.OperationId != operation.OperationId || challenge.ExpiresAt != operation.ExpiresAt
            || challenge.ExpiresAt <= DateTimeOffset.UtcNow || !TailscaleLoginUrl.IsOfficial(challenge.AuthenticationUrl))
            throw new InvalidDataException("tailscale.invalid_response");
        // The URL exists only for this explicit handoff; no property, clipboard or durable reference holds it.
        var opened = await OpenBrowserAsync(new Uri(challenge.AuthenticationUrl));
        if (!Current(generation, connection, ct)) return;
        _message = opened ? "tailscale.login.browser_opened" : "tailscale.login.browser_failed";
        StartPollingIfNeeded(); Notify();
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
                Verify(result, id, connection); _operation = result; RememberRead(id);
                if (result.Closure is not null && _pending == id) _pending = null;
            }
            Notify();
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
            _operation = result; _pending = null; _completionDetected = false; _completionRead = false;
            _message = "tailscale.login.closed"; StopPolling(); Notify();
        });
    }

    /// <summary>Reads the fresh observation, then closes only after the operator confirms.</summary>
    [RelayCommand(CanExecute = nameof(CanFinish))]
    public async Task FinishAsync()
    {
        if (!CanFinish || IsBusy) return;
        await InspectAsync();
        if (CanClose) await CloseAsync();
    }

    private Task AutoReadAsync()
    {
        if (!CanRead || Selected is not { } item) return Task.CompletedTask;
        if (_readId == item.Id && _readAt is { } at && DateTimeOffset.UtcNow - at < ReadFreshness) return Task.CompletedTask;
        return ReadAsync();
    }

    /// <summary>Reads the selected receipt and host availability again; used by the page and tests.</summary>
    public Task ObserveAsync() => ObserveAndSettleAsync();
    private void RememberRead(Guid id) { _readId = id; _readAt = DateTimeOffset.UtcNow; }
    private void StartPollingIfNeeded()
    {
        if (!_visible || _lifetime is null || _poll is not null) return;
        if (_operation is not { Closure: null, State: TailscaleLoginState.Issued or TailscaleLoginState.Unknown or TailscaleLoginState.ApprovalRequired }) return;
        if (_operation.ExpiresAt <= DateTimeOffset.UtcNow) return;
        _poll = new DispatcherTimer { Interval = PollInterval };
        _poll.Tick += async (_, _) => await PollAsync();
        _poll.Start();
    }
    private void StopPolling() { _poll?.Stop(); _poll = null; }
    private async Task PollAsync()
    {
        if (_lifetime is null || !_visible || IsBusy) return;
        if (_operation is null || _operation.Closure is not null || _operation.ExpiresAt <= DateTimeOffset.UtcNow) { StopPolling(); return; }
        if (Interlocked.CompareExchange(ref _pollGate, 1, 0) != 0) return;
        try { await ObserveAndSettleAsync(); }
        finally { Interlocked.Exchange(ref _pollGate, 0); }
    }
    private async Task ObserveAndSettleAsync()
    {
        if (_lifetime is null || IsBusy) return;
        var generation = _generation; var connection = _connection; var ct = _lifetime.Token;
        try
        {
            if (ReadBackendStateAsync is not null)
            {
                var state = await ReadBackendStateAsync(ct);
                if (generation != _generation || ct.IsCancellationRequested) return;
                var wasCompleted = _completionDetected;
                _backendState = state;
                if (!wasCompleted && _operation is { Closure: null, State: TailscaleLoginState.Issued or TailscaleLoginState.Unknown or TailscaleLoginState.ApprovalRequired }
                    && state is "Running" or "NeedsMachineAuth")
                { _completionDetected = true; _message = "tailscale.login.completed_detected"; }
                Notify();
            }
            if (_completionDetected && !_completionRead && connection is not null && Selected is not null)
            {
                var result = await client.GetAsync(connection, Selected.Id, ct);
                if (generation != _generation || ct.IsCancellationRequested) return;
                Verify(result, Selected.Id, connection); _operation = result; _recovery = null; RememberRead(Selected.Id);
                _completionRead = true; StopPolling();
                Notify();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (Handled(ex)) { if (generation == _generation && !ct.IsCancellationRequested) { _message = ProblemCode(ex) ?? _message; Notify(); } }
    }
}
