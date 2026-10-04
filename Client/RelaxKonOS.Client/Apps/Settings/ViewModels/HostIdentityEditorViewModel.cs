using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.HostSettings;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>
/// Draft/preview/apply UI over the independent remote service. The name limit and the effective state
/// come from the remote snapshot, so the client never assumes the target is its own platform. A staged
/// rename (Windows) is shown as pending until the host restarts and is never reported as live.
/// </summary>
public sealed partial class HostIdentityEditorViewModel : ObservableObject, IDisposable
{
    private readonly IHostIdentityService _service;
    private readonly IAuthSession _session;
    private readonly LocalizationService _localization;
    private HostSettingsConnection? _connection;
    private HostIdentitySnapshot? _snapshot;
    private SettingsPlan? _plan;
    private SettingsPlan? _completedPlan;
    private SettingsOperation? _completedOperation;
    private bool _submitted;
    private bool _disposed;
    private CancellationTokenSource _lifetime = new();
    private string _statusKey = "settings.hostname.load_prompt";

    public HostIdentityEditorViewModel(IHostIdentityService service, IAuthSession session, LocalizationService localization)
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
    [ObservableProperty] private string _currentHostName = "";
    [ObservableProperty] private string _pendingHostName = "";
    [ObservableProperty] private string _draftName = "";
    [ObservableProperty] private int _maximumLength;
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private string _problemCode = "";
    public string StatusText => _localization.Get(_statusKey, _statusKey);
    public string OperationId => (_plan ?? _completedPlan)?.PlanId.ToString("D") ?? "";
    public bool HasPreview => !_submitted && _plan is not null;
    public bool ShowPreviewAction => !_submitted && _plan is null && !string.IsNullOrEmpty(DraftName)
        && DraftName != PendingHostName;
    public bool ShowApplyAction => !_submitted && _plan is not null;
    public bool ShowQueryAction => _submitted;
    public bool ShowRollbackAction => !_submitted && _completedOperation?.State == SettingsOperationState.Applied;
    public bool IsCompleted => !_submitted && _plan is null && _completedOperation is not null;
    public bool HasOperation => _plan is not null || _completedPlan is not null;
    public bool HasProblem => !string.IsNullOrEmpty(ProblemCode);
    public bool HasNameProblem => !string.IsNullOrEmpty(NameProblem);
    partial void OnProblemCodeChanged(string value) => OnPropertyChanged(nameof(HasProblem));
    /// <summary>The platform stages the rename until restart, so the live name is still the old one.</summary>
    public bool RestartPending => _snapshot is not null
        && !string.Equals(CurrentHostName, PendingHostName, StringComparison.Ordinal);
    public bool CanReload => !IsBusy;
    public bool CanPreview => !IsBusy && !_submitted && _snapshot is not null && !string.IsNullOrEmpty(DraftName)
        && HostIdentityValidation.Validate(new(DraftName), MaximumLength) is null
        && !string.Equals(DraftName, PendingHostName, StringComparison.Ordinal);
    public bool CanApply => !IsBusy && !_submitted && _plan is not null && _plan.ExpiresAt > DateTimeOffset.UtcNow;
    public bool CanQuery => !IsBusy && _plan is not null;
    public bool CanRollback => !IsBusy && ShowRollbackAction && !string.IsNullOrEmpty(_completedOperation?.ObservedRevision);
    public bool CanEdit => !IsBusy && !_submitted && _snapshot is not null;

    partial void OnIsBusyChanged(bool value) => UpdateCommands();
    partial void OnDraftNameChanged(string value)
    {
        if (!_submitted) { _plan = null; PreviewText = ""; }
        OnPropertyChanged(nameof(NameProblem));
        UpdateCommands();
    }
    /// <summary>Inline syntax hint; the Server re-validates and the Helper validates again.</summary>
    public bool HasNameLengthHint => _snapshot is not null;
    public string NameLengthHint => string.Format(_localization.Get("settings.hostname.length_hint",
        "{0} / {1} characters · limit reported by the remote host"), DraftName.Length, MaximumLength);
    public string NameProblem => string.IsNullOrEmpty(DraftName) || _snapshot is null ? ""
        : DraftName.Length > MaximumLength
            ? string.Format(_localization.Get("settings.hostname.too_long",
                "Host name is too long: {0} characters, maximum {1}."), DraftName.Length, MaximumLength)
            : HostIdentityValidation.Validate(new(DraftName), MaximumLength) is null
                ? "" : _localization.Get("settings.hostname.invalid_name", "settings.hostname.invalid_name");
    partial void OnMaximumLengthChanged(int value)
    {
        OnPropertyChanged(nameof(NameProblem));
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
        _snapshot = snapshot;
        _plan = null;
        _completedPlan = null;
        _completedOperation = null;
        _submitted = false;
        CurrentHostName = snapshot.Value.HostName;
        PendingHostName = snapshot.Value.PendingHostName;
        MaximumLength = snapshot.Value.MaximumHostNameLength;
        DraftName = snapshot.Value.PendingHostName;
        PreviewText = "";
        OnPropertyChanged(nameof(RestartPending));
        SetStatus("settings.hostname.loaded");
        ProblemCode = snapshot.Capability.ReasonCode ?? "";
    });

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private Task PreviewAsync() => RunAsync(async ct =>
    {
        var connection = Connection();
        var plan = await _service.PreviewAsync(connection,
            new(_snapshot!.Value.Revision, Guid.NewGuid().ToString("N"), new(DraftName)), ct);
        if (_disposed) return;
        _plan = plan;
        PreviewText = string.Join(Environment.NewLine, plan.Differences.Select(d => $"{d.Before} → {d.After}"))
            + Environment.NewLine + _localization.Get(plan.ImpactCode, plan.ImpactCode)
            + Environment.NewLine + _localization.Get("settings.effective." + plan.EffectiveState.ToString().ToLowerInvariant(),
                plan.EffectiveState.ToString())
            + Environment.NewLine + plan.ExpiresAt.ToLocalTime().ToString("g");
        SetStatus("settings.hostname.review");
    });

    [RelayCommand(CanExecute = nameof(CanApply))]
    private Task ApplyAsync() => RunAsync(async ct =>
    {
        var connection = Connection();
        var plan = _plan!;
        // Authentication UI is invoked only by the user's explicit Apply action.
        if (RequestAuthorizationAsync is null || !await RequestAuthorizationAsync(connection))
        { SetStatus("settings.hostname.authorization_cancelled"); return; }
        ct.ThrowIfCancellationRequested();
        if (!_service.IsCurrent(connection) || _plan != plan) throw new InvalidOperationException("settings.connection_changed");
        _submitted = true;
        SetStatus("settings.hostname.applying");
        UpdateCommands();
        ShowOperation(await _service.ApplyAsync(connection, plan.PlanId, ct));
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
        { SetStatus("settings.hostname.authorization_cancelled"); return; }
        ct.ThrowIfCancellationRequested();
        if (!_service.IsCurrent(connection) || _completedPlan != plan) throw new InvalidOperationException("settings.connection_changed");
        _plan = plan;
        _submitted = true;
        SetStatus("settings.hostname.restoring");
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
            PendingHostName = (operation.State == SettingsOperationState.Applied ? difference.After : difference.Before) ?? "";
            if (operation.EffectiveState == SettingsEffectiveState.Immediate) CurrentHostName = PendingHostName;
            _completedPlan = _plan;
            _completedOperation = operation;
            PreviewText = "";
            // Preserve the live name for staged renames, and use the confirmed revision for the next edit.
            if (!string.IsNullOrEmpty(operation.ObservedRevision) && _snapshot is not null)
            {
                _snapshot = _snapshot with { Value = _snapshot.Value with
                    { HostName = CurrentHostName, PendingHostName = PendingHostName,
                      Revision = operation.ObservedRevision, ObservedAt = operation.UpdatedAt },
                    EffectiveState = operation.EffectiveState };
                _plan = null;
                DraftName = PendingHostName;
                _submitted = false;
            }
            SetStatus(operation.State == SettingsOperationState.Applied
                ? (RestartPending ? "settings.hostname.applied_pending" : "settings.hostname.applied")
                : (RestartPending ? "settings.hostname.restored_pending" : "settings.hostname.restored"));
        }
        else SetStatus("settings.operation." + operation.State.ToString().ToLowerInvariant());
        OnPropertyChanged(nameof(RestartPending));
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
            SetStatus(_submitted ? "settings.hostname.outcome_unknown" : "settings.hostname.failed");
        }
        finally { IsBusy = false; UpdateCommands(); }
    }

    private void SetStatus(string key) { _statusKey = key; OnPropertyChanged(nameof(StatusText)); }
    private void UpdateCommands()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(OperationId));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(ShowPreviewAction));
        OnPropertyChanged(nameof(ShowApplyAction));
        OnPropertyChanged(nameof(ShowQueryAction));
        OnPropertyChanged(nameof(ShowRollbackAction));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(HasOperation));
        OnPropertyChanged(nameof(HasNameProblem));
        OnPropertyChanged(nameof(NameLengthHint));
        OnPropertyChanged(nameof(HasNameLengthHint));
        ReloadCommand.NotifyCanExecuteChanged(); PreviewCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged(); QueryCommand.NotifyCanExecuteChanged(); RollbackCommand.NotifyCanExecuteChanged();
    }
    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(NameProblem));
        OnPropertyChanged(nameof(HasNameProblem));
        OnPropertyChanged(nameof(NameLengthHint));
        OnPropertyChanged(nameof(HasNameLengthHint));
    }
    private void OnSessionChanged(object? sender, AuthSessionStateChangedEventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || _connection is null || _service.IsCurrent(_connection)) return;
        _lifetime.Cancel(); _lifetime.Dispose(); _lifetime = new();
        _connection = null; _snapshot = null; _plan = null; _completedPlan = null; _completedOperation = null; _submitted = false;
        CurrentHostName = ""; PendingHostName = ""; DraftName = ""; MaximumLength = 0;
        TargetText = ""; PreviewText = ""; ProblemCode = "";
        OnPropertyChanged(nameof(RestartPending));
        SetStatus("settings.hostname.load_prompt"); UpdateCommands();
    });
    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel(); _lifetime.Dispose();
        _session.StateChanged -= OnSessionChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
    }
}
