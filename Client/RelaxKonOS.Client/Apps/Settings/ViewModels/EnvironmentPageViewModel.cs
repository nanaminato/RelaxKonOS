using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.HostSettings;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

public sealed partial class EnvironmentPageViewModel : SettingsPageViewModel, IDisposable
{
    private readonly IHostEnvironmentService _service;
    private readonly IAuthSession _session;
    private HostSettingsConnection? _connection;
    private HostEnvironmentSnapshot? _snapshot;
    private HostEnvironmentSnapshot? _userSnapshot;
    private HostEnvironmentSnapshot? _machineSnapshot;
    private SettingsPlan? _plan;
    private SettingsOperation? _operation;
    private readonly List<EnvironmentMutation> _draft = new();
    private CancellationTokenSource _lifetime = new();
    private bool _submitted, _disposed;
    public EnvironmentPageViewModel(ShellSettings settings, IHostEnvironmentService service, IAuthSession session) : base(settings, null)
    { _service = service; _session = session; session.StateChanged += SessionChanged; }
    public override string Route => "environment";
    public override string DisplayNameKey => "settings.environment.title";
    public override string DisplayName => "Environment variables";
    public Func<HostSettingsConnection, SettingsScope, HostElevationCapability, Task<bool>>? RequestAuthorizationAsync { get; set; }
    public Func<SettingsScope, EnvironmentVariable?, Task<WindowsEnvironmentMutation?>>? RequestWindowsMutationAsync { get; set; }
    public Func<SettingsScope, EnvironmentVariable, Task<bool>>? RequestWindowsDeletionConfirmationAsync { get; set; }
    public Action? RequestClose { get; set; }
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _machineScope;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _variableName = "";
    [ObservableProperty] private string _variableValue = "";
    [ObservableProperty] private bool _expandString;
    [ObservableProperty] private bool _confirmHighImpact;
    [ObservableProperty] private EnvironmentVariable? _selectedVariable;
    [ObservableProperty] private IReadOnlyList<EnvironmentVariable> _variables = Array.Empty<EnvironmentVariable>();
    [ObservableProperty] private IReadOnlyList<EnvironmentVariable> _userVariables = Array.Empty<EnvironmentVariable>();
    [ObservableProperty] private IReadOnlyList<EnvironmentVariable> _systemVariables = Array.Empty<EnvironmentVariable>();
    [ObservableProperty] private EnvironmentVariable? _selectedUserVariable;
    [ObservableProperty] private EnvironmentVariable? _selectedSystemVariable;
    [ObservableProperty] private string _draftText = "";
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private LocalizedStatus _statusText;
    [ObservableProperty] private string _problemCode = "";
    public SettingsScope Scope => MachineScope ? SettingsScope.HostMachine : SettingsScope.HostUser;
    public bool CanEdit => !IsBusy && !_submitted && _snapshot is not null;
    public bool CanLoad => !IsBusy && _draft.Count == 0 && !_submitted;
    public bool CanChangeScope => CanLoad;
    public bool CanPreview => CanEdit && _draft.Count > 0;
    public bool CanApply => !IsBusy && !_submitted && _plan is not null && _plan.ExpiresAt > DateTimeOffset.UtcNow;
    public bool CanQuery => !IsBusy && _plan is not null;
    public bool CanRollback => !IsBusy && _operation?.State == SettingsOperationState.Applied;
    public bool CanDiscard => !IsBusy && (!_submitted || _operation?.State is SettingsOperationState.Applied or SettingsOperationState.RolledBack or SettingsOperationState.Failed);
    public string TargetText => _connection is null ? "" : _connection.ServerUrl + " · " + _session.CurrentUser?.Username + " · " + _snapshot?.Target.ResourceId;
    public string ScopeHeading => MachineScope
        ? T("settings.environment.system_variables", "System variables")
        : T("settings.environment.user_variables", "User variables");
    public bool IsWindowsEnvironment => _userSnapshot is not null && !_userSnapshot.CaseSensitiveNames;
    public bool IsNotWindowsEnvironment => !IsWindowsEnvironment;
    public string SelectedDetails => SelectedVariable is not { } value ? "" : value.ValueKind + " · " + value.Source + Environment.NewLine
        + (value.ExpandedPreview ?? "") + Environment.NewLine + string.Join(Environment.NewLine, value.Warnings);
    public string OperationId => _plan?.PlanId.ToString("D") ?? "";
    partial void OnIsBusyChanged(bool value) => Update();
    partial void OnFilterChanged(string value) => RefreshVariables();
    partial void OnMachineScopeChanged(bool value)
    {
        if (IsWindowsEnvironment)
        {
            _snapshot = value ? _machineSnapshot : _userSnapshot;
            OnPropertyChanged(nameof(ScopeHeading));
            return;
        }
        Clear();
        OnPropertyChanged(nameof(ScopeHeading));
    }
    partial void OnConfirmHighImpactChanged(bool value) { _plan = null; PreviewText = ""; Update(); }
    partial void OnSelectedVariableChanged(EnvironmentVariable? value)
    {
        VariableName = value?.Name ?? "";
        VariableValue = value?.RawValue ?? "";
        ExpandString = value?.ValueKind == EnvironmentValueKind.ExpandString;
        OnPropertyChanged(nameof(SelectedDetails));
    }
    partial void OnSelectedUserVariableChanged(EnvironmentVariable? value)
    {
        if (value is null) return;
        SelectWindowsScope(SettingsScope.HostUser, value);
    }
    partial void OnSelectedSystemVariableChanged(EnvironmentVariable? value)
    {
        if (value is null) return;
        SelectWindowsScope(SettingsScope.HostMachine, value);
    }

    /// <summary>Opened once for a Windows target.  The server expands this single verified grant to
    /// the environment read/reveal/change capabilities for the current user's two environment stores.</summary>
    public Task InitializeAsync() => RunAsync(async ct =>
    {
        var connection = _service.CaptureConnection();
        _connection = connection;
        if (!await Authorize(connection, SettingsScope.HostUser, HostElevationCapability.HostEnvironmentChange, ct)) return;
        await LoadWindowsScopesAsync(connection, ct);
        StatusText = T("settings.host_time.loaded", "Loaded");
    });

    private async Task LoadWindowsScopesAsync(HostSettingsConnection connection, CancellationToken ct)
    {
        var user = await _service.ReadAsync(connection, SettingsScope.HostUser, reveal: true, ct);
        ct.ThrowIfCancellationRequested();
        if (user.CaseSensitiveNames)
        {
            _snapshot = user;
            _userSnapshot = _machineSnapshot = null;
            RefreshVariables();
            OnPropertyChanged(nameof(IsWindowsEnvironment));
            OnPropertyChanged(nameof(IsNotWindowsEnvironment));
            return;
        }
        var machine = await _service.ReadAsync(connection, SettingsScope.HostMachine, reveal: true, ct);
        ct.ThrowIfCancellationRequested();
        _userSnapshot = user;
        _machineSnapshot = machine;
        UserVariables = user.Variables;
        SystemVariables = machine.Variables;
        SelectWindowsScope(SettingsScope.HostUser, null);
        OnPropertyChanged(nameof(IsWindowsEnvironment));
        OnPropertyChanged(nameof(IsNotWindowsEnvironment));
    }

    private void SelectWindowsScope(SettingsScope scope, EnvironmentVariable? variable)
    {
        MachineScope = scope == SettingsScope.HostMachine;
        _snapshot = scope == SettingsScope.HostMachine ? _machineSnapshot : _userSnapshot;
        SelectedVariable = variable;
        if (variable is null)
        {
            VariableName = "";
            VariableValue = "";
        }
        RefreshPath();
        Update();
    }
    [RelayCommand(CanExecute = nameof(CanLoad))]
    private Task LoadAsync() => LoadCoreAsync(false);
    [RelayCommand(CanExecute = nameof(CanLoad))]
    private Task RevealAsync() => LoadCoreAsync(true);
    private Task LoadCoreAsync(bool reveal) => RunAsync(async ct =>
    {
        var connection = _service.CaptureConnection(); _connection = connection;
        if (!await Authorize(connection, HostElevationCapability.HostEnvironmentRead, ct)) return;
        if (reveal && !await Authorize(connection, HostElevationCapability.HostEnvironmentReveal, ct)) return;
        var snapshot = await _service.ReadAsync(connection, Scope, reveal, ct);
        ct.ThrowIfCancellationRequested();
        _snapshot = snapshot; SelectedVariable = null; VariableName = ""; VariableValue = ""; RefreshVariables();
        StatusText = T("settings.host_time.loaded", "Loaded");
    });

    [RelayCommand]
    private Task NewUserVariableAsync() => EditWindowsVariableAsync(SettingsScope.HostUser, null);
    [RelayCommand]
    private Task EditUserVariableAsync() => EditWindowsVariableAsync(SettingsScope.HostUser, SelectedUserVariable);
    [RelayCommand]
    private Task DeleteUserVariableAsync() => DeleteWindowsVariableAsync(SettingsScope.HostUser, SelectedUserVariable);
    [RelayCommand]
    private Task NewSystemVariableAsync() => EditWindowsVariableAsync(SettingsScope.HostMachine, null);
    [RelayCommand]
    private Task EditSystemVariableAsync() => EditWindowsVariableAsync(SettingsScope.HostMachine, SelectedSystemVariable);
    [RelayCommand]
    private Task DeleteSystemVariableAsync() => DeleteWindowsVariableAsync(SettingsScope.HostMachine, SelectedSystemVariable);

    private Task EditWindowsVariableAsync(SettingsScope scope, EnvironmentVariable? variable) => RunAsync(async ct =>
    {
        if (!IsWindowsEnvironment || RequestWindowsMutationAsync is null) return;
        var mutation = await RequestWindowsMutationAsync(scope, variable);
        if (mutation is not { } edit) return;
        await ApplyWindowsMutationAsync(scope, edit, ct);
    });

    private Task DeleteWindowsVariableAsync(SettingsScope scope, EnvironmentVariable? variable) => RunAsync(async ct =>
    {
        if (!IsWindowsEnvironment || variable is null) return;
        if (RequestWindowsDeletionConfirmationAsync is null || !await RequestWindowsDeletionConfirmationAsync(scope, variable)) return;
        await ApplyWindowsMutationAsync(scope, new(new(variable.Name, EnvironmentMutationKind.Delete), ConfirmHighImpact: false), ct);
    });

    private async Task ApplyWindowsMutationAsync(SettingsScope scope, WindowsEnvironmentMutation edit, CancellationToken ct)
    {
        var baseline = scope == SettingsScope.HostMachine ? _machineSnapshot : _userSnapshot;
        if (baseline is null) return;
        var change = new EnvironmentChangeSet([edit.Mutation], edit.ConfirmHighImpact);
        if (EnvironmentValidation.Validate(change, windows: true) is { } error) throw new InvalidOperationException(error);
        var connection = Connection();
        var plan = await _service.PreviewAsync(connection, new(scope, baseline.Revision, Guid.NewGuid().ToString("N"), change), ct);
        ct.ThrowIfCancellationRequested();
        _submitted = true;
        var operation = await _service.ApplyAsync(connection, plan.PlanId, ct);
        ct.ThrowIfCancellationRequested();
        Show(operation);
        if (operation.State == SettingsOperationState.Applied)
        {
            _submitted = false;
            await LoadWindowsScopesAsync(connection, ct);
        }
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void StageSet() => Stage(new(VariableName, EnvironmentMutationKind.Set, VariableValue,
        ExpandString ? EnvironmentValueKind.ExpandString : EnvironmentValueKind.String));
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void StageDelete() => Stage(new(VariableName, EnvironmentMutationKind.Delete));
    private void Stage(EnvironmentMutation mutation)
    {
        var windows = !_snapshot!.CaseSensitiveNames;
        // Confirmation is checked at preview time, after the user can review the staged batch.
        if (EnvironmentValidation.Validate(new(new[] { mutation }, true), windows) is { } error)
        { ProblemCode = error; return; }
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var next = _draft.Where(item => !item.Name.Equals(mutation.Name, comparison)).Append(mutation).ToArray();
        if (EnvironmentValidation.Validate(new(next, true), windows) is { } batchError) { ProblemCode = batchError; return; }
        _draft.Clear(); _draft.AddRange(next); _plan = null; PreviewText = ""; ProblemCode = "";
        RefreshDraft();
        VariableValue = ""; Update();
    }
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private Task PreviewAsync() => RunAsync(async ct =>
    {
        var change = new EnvironmentChangeSet(_draft.ToArray(), ConfirmHighImpact);
        if (EnvironmentValidation.Validate(change, !_snapshot!.CaseSensitiveNames) is { } error) throw new InvalidOperationException(error);
        var plan = await _service.PreviewAsync(Connection(), new(Scope, _snapshot.Revision, Guid.NewGuid().ToString("N"), change), ct);
        ct.ThrowIfCancellationRequested(); _plan = plan;
        PreviewText = string.Join(Environment.NewLine, plan.Differences.Select(d => d.SettingId + " · " + d.Before + " → " + d.After))
            + Environment.NewLine + T("settings.environment.effect", "New processes only") + Environment.NewLine + plan.ExpiresAt.ToLocalTime().ToString("g");
        StatusText = Ref("settings.host_time.review", "Review the plan");
    });
    [RelayCommand(CanExecute = nameof(CanApply))]
    private Task ApplyAsync() => RunAsync(async ct =>
    {
        var connection = Connection(); var plan = _plan!;
        if (!await Authorize(connection, Scope, HostElevationCapability.HostEnvironmentRead, ct)
            || !await Authorize(connection, Scope, HostElevationCapability.HostEnvironmentChange, ct)) return;
        if (_plan != plan) throw new InvalidOperationException("settings.connection_changed");
        _submitted = true; StatusText = Ref("settings.host_time.outcome_unknown", "Query the operation before retrying");
        var operation = await _service.ApplyAsync(connection, plan.PlanId, ct);
        ct.ThrowIfCancellationRequested(); Show(operation);
    });
    [RelayCommand(CanExecute = nameof(CanQuery))]
    private Task QueryAsync() => RunAsync(async ct =>
    { var operation = await _service.GetOperationAsync(Connection(), _plan!.PlanId, ct); ct.ThrowIfCancellationRequested(); Show(operation); });
    [RelayCommand(CanExecute = nameof(CanRollback))]
    private Task RollbackAsync() => RunAsync(async ct =>
    {
        var connection = Connection();
        if (!await Authorize(connection, Scope, HostElevationCapability.HostEnvironmentRead, ct)
            || !await Authorize(connection, Scope, HostElevationCapability.HostEnvironmentChange, ct)) return;
        var revision = _operation!.ObservedRevision!;
        _operation = null; // A lost rollback response must not retain the previous Applied state.
        StatusText = Ref("settings.host_time.outcome_unknown", "Query the operation before retrying");
        var operation = await _service.RollbackAsync(connection, _plan!.PlanId, revision, ct);
        ct.ThrowIfCancellationRequested(); Show(operation);
    });
    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard() => Clear();
    [RelayCommand]
    private void Close()
    {
        Clear();
        RequestClose?.Invoke();
    }
    private void Show(SettingsOperation operation)
    {
        _operation = operation; _submitted = operation.State != SettingsOperationState.Prepared;
        StatusText = Ref("settings.operation." + operation.State.ToString().ToLowerInvariant(), operation.State.ToString());
        ProblemCode = operation.ProblemCode ?? "";
        if (_submitted) { _snapshot = null; _draft.Clear(); RefreshDraft(); VariableValue = ""; Variables = Array.Empty<EnvironmentVariable>(); SelectedVariable = null; }
    }
    private Task<bool> Authorize(HostSettingsConnection connection, HostElevationCapability capability, CancellationToken ct)
        => Authorize(connection, Scope, capability, ct);
    private async Task<bool> Authorize(HostSettingsConnection connection, SettingsScope scope, HostElevationCapability capability, CancellationToken ct)
    {
        if (RequestAuthorizationAsync is null || !await RequestAuthorizationAsync(connection, scope, capability))
        { StatusText = Ref("settings.host_time.authorization_cancelled", "Authorization cancelled"); return false; }
        ct.ThrowIfCancellationRequested();
        if (!_service.IsCurrent(connection)) throw new InvalidOperationException("settings.connection_changed");
        return true;
    }
    private HostSettingsConnection Connection() => _connection is { } c && _service.IsCurrent(c) ? c : throw new InvalidOperationException("settings.connection_changed");
    private void RefreshVariables() => Variables = _snapshot?.Variables.Where(v => v.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)).ToArray() ?? Array.Empty<EnvironmentVariable>();
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy || _disposed) return;
        var lifetime = _lifetime; IsBusy = true; ProblemCode = "";
        try { await action(lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (lifetime == _lifetime && !_disposed) ProblemCode = error is RelaxKonOSAuthException auth ? $"{auth.Status} · {auth.Type} · {auth.Title}" : error.Message;
        }
        finally { IsBusy = false; Update(); }
    }
    private void Clear()
    {
        _snapshot = null; _plan = null; _operation = null; _submitted = false; _draft.Clear();
        Variables = Array.Empty<EnvironmentVariable>(); UserVariables = Array.Empty<EnvironmentVariable>(); SystemVariables = Array.Empty<EnvironmentVariable>();
        SelectedVariable = null; SelectedUserVariable = null; SelectedSystemVariable = null; VariableName = ""; VariableValue = "";
        _userSnapshot = _machineSnapshot = null;
        DraftNames = Array.Empty<string>(); SelectedDraftName = null;
        DraftText = ""; PreviewText = ""; StatusText = ""; ProblemCode = ""; ConfirmHighImpact = false; Update();
    }
    private void Update()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanChangeScope)); OnPropertyChanged(nameof(TargetText)); OnPropertyChanged(nameof(OperationId));
        OnPropertyChanged(nameof(IsWindowsEnvironment)); OnPropertyChanged(nameof(IsNotWindowsEnvironment));
        UpdatePathCommands();
        LoadCommand.NotifyCanExecuteChanged(); RevealCommand.NotifyCanExecuteChanged(); StageSetCommand.NotifyCanExecuteChanged(); StageDeleteCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged(); QueryCommand.NotifyCanExecuteChanged(); RollbackCommand.NotifyCanExecuteChanged(); DiscardCommand.NotifyCanExecuteChanged();
        EditUserVariableCommand.NotifyCanExecuteChanged(); DeleteUserVariableCommand.NotifyCanExecuteChanged(); EditSystemVariableCommand.NotifyCanExecuteChanged(); DeleteSystemVariableCommand.NotifyCanExecuteChanged();
    }
    private void SessionChanged(object? sender, AuthSessionStateChangedEventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || _connection is null || _service.IsCurrent(_connection)) return;
        _lifetime.Cancel(); _lifetime.Dispose(); _lifetime = new(); _connection = null; Clear();
    });
    public void Dispose()
    { _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); _session.StateChanged -= SessionChanged; Clear(); }
}
