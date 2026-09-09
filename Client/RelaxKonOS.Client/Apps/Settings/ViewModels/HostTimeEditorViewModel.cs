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
    private SettingsOperation? _operation;
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
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private string _problemCode = "";
    public string StatusText => _localization.Get(_statusKey, _statusKey);
    public string OperationId => _plan?.PlanId.ToString("D") ?? "";
    public bool CanReload => !IsBusy;
    public bool CanPreview => !IsBusy && !_submitted && _snapshot is not null
        && !string.IsNullOrEmpty(SelectedZone) && SelectedZone != CurrentZone;
    public bool CanApply => !IsBusy && !_submitted && _plan is not null && _plan.ExpiresAt > DateTimeOffset.UtcNow;
    public bool CanQuery => !IsBusy && _plan is not null;
    public bool CanRollback => !IsBusy && _operation?.State == SettingsOperationState.Applied;
    public bool CanEdit => !IsBusy && !_submitted;

    partial void OnIsBusyChanged(bool value) => UpdateCommands();
    partial void OnSelectedZoneChanged(string? value)
    {
        if (!_submitted) { _plan = null; PreviewText = ""; }
        UpdateCommands();
    }

    [RelayCommand(CanExecute = nameof(CanReload))]
    private Task ReloadAsync() => RunAsync(async ct =>
    {
        var connection = _service.CaptureConnection();
        _connection = connection;
        TargetText = connection.ServerUrl + " · " + _session.CurrentUser?.Username;
        var snapshot = await _service.ReadAsync(connection, ct);
        if (_disposed) return;
        _connection = connection;
        _snapshot = snapshot;
        _plan = null;
        _operation = null;
        _submitted = false;
        CurrentZone = snapshot.Value.TimeZoneId;
        AvailableZones = snapshot.Value.AvailableTimeZoneIds;
        SelectedZone = CurrentZone;
        PreviewText = "";
        SetStatus("settings.host_time.loaded");
        ProblemCode = snapshot.Capability.ReasonCode ?? "";
    });

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private Task PreviewAsync() => RunAsync(async ct =>
    {
        var connection = Connection();
        var plan = await _service.PreviewAsync(connection,
            new(_snapshot!.Value.Revision, Guid.NewGuid().ToString("N"), new(SelectedZone!)), ct);
        if (_disposed) return;
        _plan = plan;
        PreviewText = string.Join(Environment.NewLine, plan.Differences.Select(d => $"{d.Before} → {d.After}"))
            + Environment.NewLine + _localization.Get(plan.ImpactCode, plan.ImpactCode)
            + Environment.NewLine + plan.ExpiresAt.ToLocalTime().ToString("g");
        SetStatus("settings.host_time.review");
    });

    [RelayCommand(CanExecute = nameof(CanApply))]
    private Task ApplyAsync() => RunAsync(async ct =>
    {
        var connection = Connection();
        var plan = _plan!;
        // Authentication UI is invoked only by the user's explicit Apply action.
        if (RequestAuthorizationAsync is null || !await RequestAuthorizationAsync(connection))
        { SetStatus("settings.host_time.authorization_cancelled"); return; }
        ct.ThrowIfCancellationRequested();
        if (!_service.IsCurrent(connection) || _plan != plan) throw new InvalidOperationException("settings.connection_changed");
        _submitted = true;
        SetStatus("settings.host_time.outcome_unknown");
        var operation = await _service.ApplyAsync(connection, plan.PlanId, ct);
        ShowOperation(operation);
    });

    [RelayCommand(CanExecute = nameof(CanQuery))]
    private Task QueryAsync() => RunAsync(async ct => ShowOperation(await _service.GetOperationAsync(Connection(), _plan!.PlanId, ct)));

    [RelayCommand(CanExecute = nameof(CanRollback))]
    private Task RollbackAsync() => RunAsync(async ct =>
    {
        var connection = Connection();
        if (RequestAuthorizationAsync is null || !await RequestAuthorizationAsync(connection))
        { SetStatus("settings.host_time.authorization_cancelled"); return; }
        ct.ThrowIfCancellationRequested();
        SetStatus("settings.host_time.outcome_unknown");
        ShowOperation(await _service.RollbackAsync(connection, _plan!.PlanId, _operation!.ObservedRevision!, ct));
    });

    private void ShowOperation(SettingsOperation operation)
    {
        if (_disposed) return;
        _operation = operation;
        // A rejected precondition can be reauthorized using the same immutable plan.
        _submitted = operation.State != SettingsOperationState.Prepared;
        if (operation.State == SettingsOperationState.Applied) CurrentZone = _plan!.Differences.Single().After ?? "";
        if (operation.State == SettingsOperationState.RolledBack) CurrentZone = _plan!.Differences.Single().Before ?? "";
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
            SetStatus(_submitted ? "settings.host_time.outcome_unknown" : "settings.host_time.failed");
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
    private void OnLanguageChanged(object? sender, EventArgs args) => OnPropertyChanged(nameof(StatusText));
    private void OnSessionChanged(object? sender, AuthSessionStateChangedEventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || _connection is null || _service.IsCurrent(_connection)) return;
        _lifetime.Cancel(); _lifetime.Dispose(); _lifetime = new();
        _connection = null; _snapshot = null; _plan = null; _operation = null; _submitted = false;
        CurrentZone = ""; AvailableZones = Array.Empty<string>(); SelectedZone = null; TargetText = ""; PreviewText = ""; ProblemCode = "";
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
