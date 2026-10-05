using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.HostSettings;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>Draft/preview/apply UI over the independent remote service; never reads client OS time zone.</summary>
public sealed partial class HostTimeEditorViewModel : ObservableObject, IDisposable
{
    private readonly IHostTimeService _service;
    private readonly IAuthSession _session;
    private readonly LocalizationService _localization;
    private HostSettingsConnection? _connection;
    private HostTimeSnapshot? _snapshot;
    private SettingsPlan? _plan;
    private SettingsPlan? _completedPlan;
    private SettingsOperation? _completedOperation;
    private bool _submitted;
    private bool _disposed;
    private CancellationTokenSource _lifetime = new();
    private string _statusKey = "settings.host_time.load_prompt";

    public HostTimeEditorViewModel(IHostTimeService service, IAuthSession session, LocalizationService localization)
    {
        _service = service;
        _session = session;
        _localization = localization;
        session.StateChanged += OnSessionChanged;
        localization.LanguageChanged += OnLanguageChanged;
    }

    public Func<HostSettingsConnection, Task<bool>>? RequestAuthorizationAsync { get; set; }
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _targetText = "";
    [ObservableProperty] private string _currentZone = "";
    [ObservableProperty] private IReadOnlyList<string> _availableZones = Array.Empty<string>();
    [ObservableProperty] private string? _selectedZone;
    [ObservableProperty] private string _problemCode = "";
    // Labels are presentation only; writes always use an ID from the remote catalog.
    public IReadOnlyList<string> ZoneLabels => AvailableZones.Select(ZoneLabel).ToArray();
    public string CurrentZoneLabel => string.IsNullOrEmpty(CurrentZone) ? "" : ZoneLabel(CurrentZone);
    public string? SelectedZoneLabel
    {
        get => SelectedZone is null ? null : ZoneLabel(SelectedZone);
        set => SelectedZone = AvailableZones.FirstOrDefault(id => ZoneLabel(id) == value);
    }
    public bool HasDraft => _snapshot is not null && !_submitted && SelectedZone != CurrentZone;
    private bool CanResetDraft() => !IsBusy && HasDraft;

    [RelayCommand(CanExecute = nameof(CanResetDraft))]
    private void ResetDraft()
    {
        if (!CanResetDraft()) return;
        SelectedZone = CurrentZone;
        ProblemCode = "";
        SetStatus("settings.host_time.draft_reset");
        UpdateCommands();
    }

    public bool ShowApplyAction => HasDraft;
    public bool ShowQueryAction => _submitted;
    public bool ShowRollbackAction => !_submitted && _completedOperation?.State == SettingsOperationState.Applied;
    public bool IsCompleted => !_submitted && !HasDraft && _plan is null && _completedOperation is not null;
    public bool HasProblem => !string.IsNullOrEmpty(ProblemCode);
    public bool HasOperation => _plan is not null || _completedPlan is not null;

    private static string ZoneLabel(string id)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return zone.DisplayName.Contains(id, StringComparison.OrdinalIgnoreCase)
                ? zone.DisplayName : $"{zone.DisplayName} · {id}";
        }
        catch (TimeZoneNotFoundException) { return id; }
        catch (InvalidTimeZoneException) { return id; }
    }

    partial void OnAvailableZonesChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(ZoneLabels));
    partial void OnCurrentZoneChanged(string value) => OnPropertyChanged(nameof(CurrentZoneLabel));
    partial void OnProblemCodeChanged(string value) => OnPropertyChanged(nameof(HasProblem));
    public string StatusText => _localization.Get(_statusKey, _statusKey);
    public string OperationId => (_plan ?? _completedPlan)?.PlanId.ToString("D") ?? "";
    public bool CanReload => !IsBusy && !_submitted;
    public bool CanApply => !IsBusy && !_submitted && _snapshot is not null
        && SelectedZone is not null && AvailableZones.Contains(SelectedZone, StringComparer.Ordinal) && SelectedZone != CurrentZone;
    public bool CanQuery => !IsBusy && _plan is not null;
    public bool CanRollback => !IsBusy && ShowRollbackAction && !string.IsNullOrEmpty(_completedOperation?.ObservedRevision);
    public bool CanEdit => !IsBusy && !_submitted && _snapshot is not null;

    partial void OnIsBusyChanged(bool value) => UpdateCommands();
    partial void OnSelectedZoneChanged(string? value)
    {
        if (!_submitted) { _plan = null; }
        OnPropertyChanged(nameof(SelectedZoneLabel));
        UpdateCommands();
    }

    [RelayCommand(CanExecute = nameof(CanReload))]
    private Task ReloadAsync() => RunAsync(async ct =>
    {
        var connection = _service.CaptureConnection();
        _connection = connection;
        TargetText = connection.ServiceId + " · " + _session.CurrentUser?.Username;
        var snapshot = await _service.ReadAsync(connection, ct);
        if (_disposed) return;
        _connection = connection;
        _snapshot = snapshot;
        _plan = null;
        _completedPlan = null;
        _completedOperation = null;
        _submitted = false;
        CurrentZone = snapshot.Value.TimeZoneId;
        AvailableZones = snapshot.Value.AvailableTimeZoneIds;
        SelectedZone = CurrentZone;

        SetStatus("settings.host_time.loaded");
        ProblemCode = snapshot.Capability.ReasonCode ?? "";
    });

    [RelayCommand(CanExecute = nameof(CanApply))]
    private Task ApplyAsync() => RunAsync(async ct =>
    {
        var connection = Connection();
        // Preparation remains a server contract, not a separate user action.
        var plan = _plan;
        if (plan is null || plan.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            plan = await _service.PreviewAsync(connection,
                new(_snapshot!.Value.Revision, Guid.NewGuid().ToString("N"), new(SelectedZone!)), ct);
            ct.ThrowIfCancellationRequested();
            if (!_service.IsCurrent(connection)) throw new InvalidOperationException("settings.connection_changed");
            _plan = plan;
        }
        // Authentication UI is invoked only by the user's explicit Apply action.
        if (RequestAuthorizationAsync is null || !await RequestAuthorizationAsync(connection))
        { SetStatus("settings.host_time.authorization_cancelled"); return; }
        ct.ThrowIfCancellationRequested();
        if (!_service.IsCurrent(connection) || _plan != plan) throw new InvalidOperationException("settings.connection_changed");
        _submitted = true;
        SetStatus("settings.host_time.applying");
        UpdateCommands();
        var operation = await _service.ApplyAsync(connection, plan.PlanId, ct);
        ShowOperation(operation);
    });

    [RelayCommand(CanExecute = nameof(CanQuery))]
    private Task QueryAsync() => RunAsync(async ct => ShowOperation(await _service.GetOperationAsync(Connection(), _plan!.PlanId, ct)));

    [RelayCommand(CanExecute = nameof(CanRollback))]
    private Task RollbackAsync() => RunAsync(async ct =>
    {
        var connection = Connection();
        var plan = _completedPlan!;
        var operation = _completedOperation!;
        if (RequestAuthorizationAsync is null || !await RequestAuthorizationAsync(connection))
        { SetStatus("settings.host_time.authorization_cancelled"); return; }
        ct.ThrowIfCancellationRequested();
        if (!_service.IsCurrent(connection) || _completedPlan != plan) throw new InvalidOperationException("settings.connection_changed");
        _plan = plan;
        _submitted = true;
        SetStatus("settings.host_time.restoring");
        UpdateCommands();
        ShowOperation(await _service.RollbackAsync(connection, plan.PlanId, operation.ObservedRevision!, ct));
    });

    private void ShowOperation(SettingsOperation operation)
    {
        if (_disposed) return;
        // A rejected precondition can be reauthorized using the same immutable plan.
        _submitted = operation.State != SettingsOperationState.Prepared;
        ProblemCode = operation.ProblemCode ?? "";
        if (operation.State is SettingsOperationState.Applied or SettingsOperationState.RolledBack)
        {
            var difference = _plan!.Differences.Single();
            CurrentZone = (operation.State == SettingsOperationState.Applied ? difference.After : difference.Before) ?? "";
            _completedPlan = _plan;
            _completedOperation = operation;

            // Continue editing only with the revision confirmed by the remote operation.
            if (!string.IsNullOrEmpty(operation.ObservedRevision) && _snapshot is not null)
            {
                _snapshot = _snapshot with { Value = _snapshot.Value with
                    { TimeZoneId = CurrentZone, Revision = operation.ObservedRevision, ObservedAt = operation.UpdatedAt } };
                _plan = null;
                SelectedZone = CurrentZone;
                _submitted = false;
            }
            SetStatus(operation.State == SettingsOperationState.Applied
                ? "settings.host_time.applied" : "settings.host_time.restored");
        }
        else SetStatus("settings.operation." + operation.State.ToString().ToLowerInvariant());
        UpdateCommands();
    }

    private HostSettingsConnection Connection() => _connection is { } connection && _service.IsCurrent(connection)
        ? connection : throw new InvalidOperationException("settings.connection_changed");

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy || _disposed) return;
        var lifetime = _lifetime;
        IsBusy = true;
        ProblemCode = "";
        try { await action(lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (lifetime != _lifetime || _disposed) return;
            ProblemCode = error is RelaxKonOSAuthException auth ? $"{auth.Status} · {auth.Type} · {auth.Title}" : error.Message;
            SetStatus(_submitted ? "settings.host_time.outcome_unknown" : "settings.host_time.failed");
        }
        finally { IsBusy = false; UpdateCommands(); }
    }

    private void SetStatus(string key) { _statusKey = key; OnPropertyChanged(nameof(StatusText)); }
    private void UpdateCommands()
    {
        OnPropertyChanged(nameof(HasDraft));
        ResetDraftCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(OperationId));
        OnPropertyChanged(nameof(ShowApplyAction));
        OnPropertyChanged(nameof(ShowQueryAction));
        OnPropertyChanged(nameof(ShowRollbackAction));
        OnPropertyChanged(nameof(HasOperation));
        OnPropertyChanged(nameof(IsCompleted));
        ReloadCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged(); QueryCommand.NotifyCanExecuteChanged(); RollbackCommand.NotifyCanExecuteChanged();
    }
    private void OnLanguageChanged(object? sender, EventArgs args) => OnPropertyChanged(nameof(StatusText));
    private void OnSessionChanged(object? sender, AuthSessionStateChangedEventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || _connection is null || _service.IsCurrent(_connection)) return;
        _lifetime.Cancel(); _lifetime.Dispose(); _lifetime = new();
        _connection = null; _snapshot = null; _plan = null; _completedPlan = null; _completedOperation = null; _submitted = false;
        CurrentZone = ""; AvailableZones = Array.Empty<string>(); SelectedZone = null; TargetText = ""; ProblemCode = "";
        SetStatus("settings.host_time.load_prompt"); UpdateCommands();
    });
    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel(); _lifetime.Dispose();
        _session.StateChanged -= OnSessionChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
    }
}
