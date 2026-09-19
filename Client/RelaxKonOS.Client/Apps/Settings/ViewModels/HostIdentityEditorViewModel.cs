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
    private SettingsOperation? _operation;
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
    public string OperationId => _plan?.PlanId.ToString("D") ?? "";
    /// <summary>The platform stages the rename until restart, so the live name is still the old one.</summary>
    public bool RestartPending => _snapshot is { } snapshot
        && !string.Equals(snapshot.Value.HostName, snapshot.Value.PendingHostName, StringComparison.Ordinal);
    public bool CanReload => !IsBusy;
    public bool CanPreview => !IsBusy && !_submitted && _snapshot is not null && !string.IsNullOrEmpty(DraftName)
        && HostIdentityValidation.Validate(new(DraftName), MaximumLength) is null
        && !string.Equals(DraftName, PendingHostName, StringComparison.Ordinal);
    public bool CanApply => !IsBusy && !_submitted && _plan is not null && _plan.ExpiresAt > DateTimeOffset.UtcNow;
    public bool CanQuery => !IsBusy && _plan is not null;
    public bool CanRollback => !IsBusy && _operation?.State == SettingsOperationState.Applied;
    public bool CanEdit => !IsBusy && !_submitted;

    partial void OnIsBusyChanged(bool value) => UpdateCommands();
    partial void OnDraftNameChanged(string value)
    {
        if (!_submitted) { _plan = null; PreviewText = ""; }
        OnPropertyChanged(nameof(NameProblem));
        UpdateCommands();
    }
    /// <summary>Inline syntax hint; the Server re-validates and the Helper validates again.</summary>
    public string NameProblem => string.IsNullOrEmpty(DraftName) ? ""
        : HostIdentityValidation.Validate(new(DraftName), MaximumLength) is null
            ? "" : _localization.Get("settings.hostname.invalid_name", "settings.hostname.invalid_name");

    [RelayCommand(CanExecute = nameof(CanReload))]
    private Task ReloadAsync() => RunAsync(async ct =>
    {
        var connection = _service.CaptureConnection();
        _connection = connection;
        TargetText = connection.ServerUrl + " · " + _session.CurrentUser?.Username;
        var snapshot = await _service.ReadAsync(connection, ct);
        if (_disposed) return;
        _snapshot = snapshot;
        _plan = null;
        _operation = null;
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
        SetStatus("settings.hostname.outcome_unknown");
        ShowOperation(await _service.ApplyAsync(connection, plan.PlanId, ct));
    });

    [RelayCommand(CanExecute = nameof(CanQuery))]
    private Task QueryAsync() => RunAsync(async ct => ShowOperation(await _service.GetOperationAsync(Connection(), _plan!.PlanId, ct)));

    [RelayCommand(CanExecute = nameof(CanRollback))]
    private Task RollbackAsync() => RunAsync(async ct =>
    {
        var connection = Connection();
        if (RequestAuthorizationAsync is null || !await RequestAuthorizationAsync(connection))
        { SetStatus("settings.hostname.authorization_cancelled"); return; }
        ct.ThrowIfCancellationRequested();
        SetStatus("settings.hostname.outcome_unknown");
        ShowOperation(await _service.RollbackAsync(connection, _plan!.PlanId, _operation!.ObservedRevision!, ct));
    });

    private void ShowOperation(SettingsOperation operation)
    {
        if (_disposed) return;
        _operation = operation;
        // A rejected precondition can be reauthorized using the same immutable plan.
        _submitted = operation.State != SettingsOperationState.Prepared;
        if (operation.State == SettingsOperationState.Applied) PendingHostName = _plan!.Differences.Single().After ?? "";
        if (operation.State == SettingsOperationState.RolledBack) PendingHostName = _plan!.Differences.Single().Before ?? "";
        if (operation.State is SettingsOperationState.Applied or SettingsOperationState.RolledBack)
            DraftName = PendingHostName;
        OnPropertyChanged(nameof(RestartPending));
        ProblemCode = operation.ProblemCode ?? "";
        SetStatus("settings.operation." + operation.State.ToString().ToLowerInvariant());
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
        ReloadCommand.NotifyCanExecuteChanged(); PreviewCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged(); QueryCommand.NotifyCanExecuteChanged(); RollbackCommand.NotifyCanExecuteChanged();
    }
    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(NameProblem));
    }
    private void OnSessionChanged(object? sender, AuthSessionStateChangedEventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || _connection is null || _service.IsCurrent(_connection)) return;
        _lifetime.Cancel(); _lifetime.Dispose(); _lifetime = new();
        _connection = null; _snapshot = null; _plan = null; _operation = null; _submitted = false;
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
