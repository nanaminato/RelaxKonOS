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
    private readonly List<EnvironmentMutation> _draft = new();
    private CancellationTokenSource _lifetime = new();
    private bool _submitted, _disposed;
    public EnvironmentPageViewModel(ShellSettings settings, IHostEnvironmentService service, IAuthSession session) : base(settings, null)
    { _service = service; _session = session; session.StateChanged += SessionChanged; }
    public override string Route => "environment";
    public override string DisplayNameKey => "settings.environment.title";
    public override string DisplayName => "Environment variables";
    public Func<HostSettingsConnection, SettingsScope, HostElevationCapability, Task<bool>>? RequestAuthorizationAsync { get; set; }
    public Func<SettingsScope, EnvironmentVariable?, Task<EnvironmentMutation?>>? RequestEnvironmentMutationAsync { get; set; }
    public Func<SettingsScope, EnvironmentVariable, Task<bool>>? RequestEnvironmentDeletionConfirmationAsync { get; set; }
    public Action? RequestClose { get; set; }
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _machineScope;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _variableName = "";
    [ObservableProperty] private string _variableValue = "";
    [ObservableProperty] private bool _expandString;
    [ObservableProperty] private EnvironmentVariable? _selectedVariable;
    [ObservableProperty] private IReadOnlyList<EnvironmentVariable> _variables = Array.Empty<EnvironmentVariable>();
    [ObservableProperty] private IReadOnlyList<EnvironmentVariable> _userVariables = Array.Empty<EnvironmentVariable>();
    [ObservableProperty] private IReadOnlyList<EnvironmentVariable> _systemVariables = Array.Empty<EnvironmentVariable>();
    [ObservableProperty] private EnvironmentVariable? _selectedUserVariable;
    [ObservableProperty] private EnvironmentVariable? _selectedSystemVariable;
    [ObservableProperty] private string _draftText = "";
    [ObservableProperty] private LocalizedStatus _statusText;
    [ObservableProperty] private string _problemCode = "";
    public SettingsScope Scope => MachineScope ? SettingsScope.HostMachine : SettingsScope.HostUser;
    public bool CanEdit => !IsBusy && !_submitted && _snapshot is not null;
    public bool CanLoad => !IsBusy && _draft.Count == 0 && !_submitted;
    public bool CanChangeScope => CanLoad;
    public bool CanApply => CanEdit && _draft.Count > 0;
    public string TargetText => _connection is null ? "" : _connection.ServiceId + " · " + _session.CurrentUser?.Username + " · " + _snapshot?.Target.ResourceId;
    public string ScopeHeading => MachineScope
        ? IsLinuxPamEnvironment
            ? T("settings.environment.pam_login_environment", "PAM login environment")
            : T("settings.environment.system_variables", "System variables")
        : T("settings.environment.user_variables", "User variables");
    public bool IsWindowsEnvironment => _userSnapshot is not null && !_userSnapshot.CaseSensitiveNames;
    public bool IsNotWindowsEnvironment => !IsWindowsEnvironment;
    public bool IsLinuxPamEnvironment => _snapshot?.Provider == "linux-pam-environment";
    public string EnvironmentEffectText => _snapshot?.EffectiveState == SettingsEffectiveState.NewLogin
        ? T("settings.environment.pam_login_effect", "Changes apply to new PAM login sessions only. They do not update running processes or system services.")
        : T("settings.environment.effect", "Saved changes affect new processes. Running processes retain their environment.");
    /// <summary>True only after the administrator grant has succeeded and the unmasked snapshot is loaded.</summary>
    public bool HasLoadedEnvironment => _snapshot is not null;
    public string SelectedDetails => SelectedVariable is not { } value ? "" : value.ValueKind + " · " + value.Source + Environment.NewLine
        + (value.ExpandedPreview ?? "") + Environment.NewLine + string.Join(Environment.NewLine, value.Warnings);
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

    /// <summary>Open the machine store first. Windows elevation expands to both registry stores;
    /// Linux has only the explicitly supported PAM machine-login store.</summary>
    public Task InitializeAsync() => RunAsync(async ct =>
    {
        var connection = _service.CaptureConnection();
        _connection = connection;
        if (!await Authorize(connection, SettingsScope.HostMachine, HostElevationCapability.HostEnvironmentChange, ct)) return;
        await LoadHostScopesAsync(connection, ct);
        StatusText = T("settings.host_time.loaded", "Loaded");
    });

    private async Task LoadHostScopesAsync(HostSettingsConnection connection, CancellationToken ct)
    {
        var machine = await _service.ReadAsync(connection, SettingsScope.HostMachine, reveal: true, ct);
        ct.ThrowIfCancellationRequested();
        if (machine.CaseSensitiveNames)
        {
            MachineScope = true;
            _snapshot = machine;
            _userSnapshot = _machineSnapshot = null;
            RefreshVariables();
            OnPropertyChanged(nameof(IsWindowsEnvironment));
            OnPropertyChanged(nameof(IsNotWindowsEnvironment));
            OnPropertyChanged(nameof(IsLinuxPamEnvironment));
            OnPropertyChanged(nameof(EnvironmentEffectText));
            OnPropertyChanged(nameof(ScopeHeading));
            return;
        }
        var user = await _service.ReadAsync(connection, SettingsScope.HostUser, reveal: true, ct);
        ct.ThrowIfCancellationRequested();
        _userSnapshot = user;
        _machineSnapshot = machine;
        UserVariables = user.Variables;
        SystemVariables = machine.Variables;
        SelectWindowsScope(SettingsScope.HostUser, null);
        OnPropertyChanged(nameof(IsWindowsEnvironment));
        OnPropertyChanged(nameof(IsNotWindowsEnvironment));
        OnPropertyChanged(nameof(IsLinuxPamEnvironment));
        OnPropertyChanged(nameof(EnvironmentEffectText));
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
    private Task NewUserVariableAsync() => EditEnvironmentVariableAsync(SettingsScope.HostUser, null);
    [RelayCommand]
    private Task EditUserVariableAsync() => EditEnvironmentVariableAsync(SettingsScope.HostUser, SelectedUserVariable);
    [RelayCommand]
    private Task DeleteUserVariableAsync() => DeleteEnvironmentVariableAsync(SettingsScope.HostUser, SelectedUserVariable);
    [RelayCommand]
    private Task NewSystemVariableAsync() => EditEnvironmentVariableAsync(SettingsScope.HostMachine, null);
    [RelayCommand]
    private Task EditSystemVariableAsync() => EditEnvironmentVariableAsync(SettingsScope.HostMachine, SelectedSystemVariable);
    [RelayCommand]
    private Task DeleteSystemVariableAsync() => DeleteEnvironmentVariableAsync(SettingsScope.HostMachine, SelectedSystemVariable);

    private Task EditEnvironmentVariableAsync(SettingsScope scope, EnvironmentVariable? variable) => RunAsync(async ct =>
    {
        if (!IsWindowsEnvironment || RequestEnvironmentMutationAsync is null) return;
        var mutation = await RequestEnvironmentMutationAsync(scope, variable);
        if (mutation is not { } edit) return;
        await ApplyEnvironmentMutationAsync(scope, edit, ct);
    });

    private Task DeleteEnvironmentVariableAsync(SettingsScope scope, EnvironmentVariable? variable) => RunAsync(async ct =>
    {
        if (!IsWindowsEnvironment || variable is null) return;
        if (RequestEnvironmentDeletionConfirmationAsync is null || !await RequestEnvironmentDeletionConfirmationAsync(scope, variable)) return;
        await ApplyEnvironmentMutationAsync(scope, new(variable.Name, EnvironmentMutationKind.Delete), ct);
    });

    private async Task ApplyEnvironmentMutationAsync(SettingsScope scope, EnvironmentMutation mutation, CancellationToken ct)
    {
        var baseline = scope == SettingsScope.HostMachine ? _machineSnapshot : _userSnapshot;
        if (baseline is null) return;
        var change = new EnvironmentChangeSet([mutation], ConfirmHighImpact: true);
        if (EnvironmentValidation.Validate(change, windows: true) is { } error) throw new InvalidOperationException(error);
        var connection = Connection();
        var plan = await _service.PreviewAsync(connection, new(scope, baseline.Revision, Guid.NewGuid().ToString("N"), change), ct);
        ct.ThrowIfCancellationRequested();
        _submitted = true;
        try
        {
            var operation = await _service.ApplyAsync(connection, plan.PlanId, ct);
            ct.ThrowIfCancellationRequested();
            StatusText = Ref("settings.operation." + operation.State.ToString().ToLowerInvariant(), operation.State.ToString());
            ProblemCode = operation.ProblemCode ?? "";
            if (operation.State == SettingsOperationState.Applied)
                await LoadHostScopesAsync(connection, ct);
        }
        finally { _submitted = false; }
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task NewVariableAsync() => EditBatchVariableAsync(null);
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task EditVariableAsync() => SelectedVariable is { } variable ? EditBatchVariableAsync(variable) : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task DeleteVariableAsync() => RunAsync(async ct =>
    {
        if (IsWindowsEnvironment || SelectedVariable is not { } variable || RequestEnvironmentDeletionConfirmationAsync is null
            || !await RequestEnvironmentDeletionConfirmationAsync(Scope, variable)) return;
        Stage(new(variable.Name, EnvironmentMutationKind.Delete));
        ct.ThrowIfCancellationRequested();
    });
    private Task EditBatchVariableAsync(EnvironmentVariable? variable) => RunAsync(async ct =>
    {
        if (IsWindowsEnvironment || RequestEnvironmentMutationAsync is null) return;
        var mutation = await RequestEnvironmentMutationAsync(Scope, variable);
        if (mutation is { } edit) Stage(edit);
        ct.ThrowIfCancellationRequested();
    });
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void StageSet() => Stage(new(VariableName, EnvironmentMutationKind.Set, VariableValue,
        ExpandString ? EnvironmentValueKind.ExpandString : EnvironmentValueKind.String));
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void StageDelete() => Stage(new(VariableName, EnvironmentMutationKind.Delete));
    private void Stage(EnvironmentMutation mutation)
    {
        var windows = !_snapshot!.CaseSensitiveNames;
        // Each staged mutation is validated immediately; the complete batch is validated again when applied.
        if (EnvironmentValidation.Validate(new(new[] { mutation }, true), windows) is { } error)
        { ProblemCode = error; return; }
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var next = _draft.Where(item => !item.Name.Equals(mutation.Name, comparison)).Append(mutation).ToArray();
        if (EnvironmentValidation.Validate(new(next, true), windows) is { } batchError) { ProblemCode = batchError; return; }
        _draft.Clear(); _draft.AddRange(next); ProblemCode = "";
        RefreshDraft();
        VariableValue = ""; Update();
    }
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var applied = false;
        await RunAsync(async ct =>
        {
            var connection = Connection();
            if (!await Authorize(connection, Scope, HostElevationCapability.HostEnvironmentRead, ct)
                || !await Authorize(connection, Scope, HostElevationCapability.HostEnvironmentChange, ct)) return;
            var change = new EnvironmentChangeSet(_draft.ToArray(), ConfirmHighImpact: true);
            if (EnvironmentValidation.Validate(change, !_snapshot!.CaseSensitiveNames) is { } error) throw new InvalidOperationException(error);
            var plan = await _service.PreviewAsync(connection, new(Scope, _snapshot.Revision, Guid.NewGuid().ToString("N"), change), ct);
            ct.ThrowIfCancellationRequested();
            _submitted = true;
            try
            {
                var operation = await _service.ApplyAsync(connection, plan.PlanId, ct);
                ct.ThrowIfCancellationRequested();
                applied = operation.State == SettingsOperationState.Applied;
                if (!applied)
                {
                    StatusText = Ref("settings.operation." + operation.State.ToString().ToLowerInvariant(), operation.State.ToString());
                    ProblemCode = operation.ProblemCode ?? "";
                }
            }
            finally { _submitted = false; }
        });
        if (applied) Close();
    }
    [RelayCommand]
    private void Close()
    {
        Clear();
        RequestClose?.Invoke();
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
        _snapshot = null; _submitted = false; _draft.Clear();
        Variables = Array.Empty<EnvironmentVariable>(); UserVariables = Array.Empty<EnvironmentVariable>(); SystemVariables = Array.Empty<EnvironmentVariable>();
        SelectedVariable = null; SelectedUserVariable = null; SelectedSystemVariable = null; VariableName = ""; VariableValue = "";
        _userSnapshot = _machineSnapshot = null;
        DraftNames = Array.Empty<string>(); SelectedDraftName = null;
        DraftText = ""; StatusText = ""; ProblemCode = ""; Update();
    }
    private void Update()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanChangeScope)); OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(IsWindowsEnvironment)); OnPropertyChanged(nameof(IsNotWindowsEnvironment));
        OnPropertyChanged(nameof(IsLinuxPamEnvironment)); OnPropertyChanged(nameof(EnvironmentEffectText)); OnPropertyChanged(nameof(ScopeHeading));
        OnPropertyChanged(nameof(HasLoadedEnvironment));
        UpdatePathCommands();
        LoadCommand.NotifyCanExecuteChanged(); RevealCommand.NotifyCanExecuteChanged(); StageSetCommand.NotifyCanExecuteChanged(); StageDeleteCommand.NotifyCanExecuteChanged();
        NewVariableCommand.NotifyCanExecuteChanged(); EditVariableCommand.NotifyCanExecuteChanged(); DeleteVariableCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
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
