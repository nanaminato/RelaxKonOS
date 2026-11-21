using RelaxKonOS.AppSDK;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.ProcessGuardian;

namespace RelaxKonOS.App.ProcessGuardian;

public sealed partial class ProcessGuardianViewModel(IServerGuardian client,
    Func<string?> username, Func<bool> isWindows, Func<string, string> localize,
    Func<string?, string?> helperProblem) : ObservableObject
{
    // The Guardian Agent accepts one local pipe connection at a time. Keep UI refreshes
    // serialized too, otherwise concurrent status/list requests can time out and replace
    // a freshly saved workload list with an empty response.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private int _activeRefreshes;
    private long _definitionLoadVersion;
    private ProcessDefinitionDto? _loadedDefinition;
    private bool _suppressSelectionDefinitionLoad;
    public ObservableCollection<GuardianWorkloadDto> Workloads { get; } = [];
    [ObservableProperty] private GuardianWorkloadDto? _selectedWorkload;
    private Func<string>? _status;
    public string StatusText => _status?.Invoke() ?? localize("guardian.status.loading");

    public void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs args) =>
        OnPropertyChanged(string.Empty);

    private void SetStatus(Func<string> status)
    {
        _status = status;
        OnPropertyChanged(nameof(StatusText));
    }

    private Func<string> Message(string key, params object?[] args) => () =>
        args.Length == 0 ? localize(key) : string.Format(CultureInfo.CurrentCulture, localize(key), args);
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _definitionId = string.Empty;
    [ObservableProperty] private string _definitionName = string.Empty;
    [ObservableProperty] private string _executablePath = string.Empty;
    [ObservableProperty] private string _workingDirectory = string.Empty;
    [ObservableProperty] private string _argumentsText = string.Empty;
    [ObservableProperty] private string _runAs = string.Empty;
    [ObservableProperty] private bool _enabledOnBoot;

    [ObservableProperty] private bool _agentNeedsInstallation;
    public Action? OpenComponentManagement { get; set; }
    [RelayCommand] private void ManageInstallation() => OpenComponentManagement?.Invoke();
    public Func<bool, Task>? ShowEditorAsync { get; set; }
    public Func<GuardianWorkloadDto, Task>? ShowLogsAsync { get; set; }
    public Func<Task>? CloseEditorAsync { get; set; }
    /// <summary>Provided by the window to surface unavailable privileged operations prominently.</summary>
    public Func<string?, Task>? ShowPrivilegedHelperUnavailableAsync { get; set; }
    /// <summary>Provided by the window and called only for a cross-account RunAs change.</summary>
    public Func<ProcessDefinitionDto, Task<GuardianAgentResponse?>>? SaveWithAdministratorApprovalAsync { get; set; }

    public async Task StartAsync() => await RefreshAsync();
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await _refreshGate.WaitAsync();
        Interlocked.Increment(ref _activeRefreshes);
        IsLoading = true;
        try
        {
            // These are deliberately sequential: the Agent's pipe server has one instance.
            var status = await client.GetStatusAsync();
            AgentNeedsInstallation = status.ProblemCode is "guardian.agent_not_configured" or "guardian.agent_not_installed";
            var workloads = status.IsInstalled ? await client.ListWorkloadsAsync() : Array.Empty<GuardianWorkloadDto>();
            SetStatus(status.IsInstalled
                ? Message("guardian.status.available", status.Version ?? "")
                : helperProblem(status.ProblemCode) is { } helperMessage
                    ? () => helperProblem(status.ProblemCode) ?? helperMessage
                    : status.ProblemCode is "guardian.agent_not_configured" or "guardian.agent_not_installed"
                    ? Message("guardian.status.install_required")
                    : Message("guardian.status.unavailable", status.ProblemCode));
            if (!status.IsInstalled) await ShowPrivilegedHelperUnavailableAsyncIfNeeded(status.ProblemCode);
            Workloads.Clear(); foreach (var workload in workloads) Workloads.Add(workload);
        }
        catch (Exception exception)
        {
            SetStatus(Message("guardian.status.failed", exception.Message));
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeRefreshes) == 0)
                IsLoading = false;
            _refreshGate.Release();
        }
    }
    [RelayCommand] private Task OpenCreateWorkloadAsync()
    {
        ClearDefinition();
        return ShowEditorAsync?.Invoke(false) ?? Task.CompletedTask;
    }
    [RelayCommand] private async Task EditWorkloadAsync(GuardianWorkloadDto? workload)
    {
        workload ??= SelectedWorkload;
        if (workload is null) return;

        // An action button does not reliably select its DataGrid row first. Select it here
        // and suppress the selection callback so one edit produces exactly one pipe call.
        _suppressSelectionDefinitionLoad = true;
        try { SelectedWorkload = workload; }
        finally { _suppressSelectionDefinitionLoad = false; }

        if (!await LoadDefinitionAsync(workload.Id))
        {
            SetStatus(Message("guardian.definition_load_failed"));
            return;
        }
        await (ShowEditorAsync?.Invoke(true) ?? Task.CompletedTask);
    }
    [RelayCommand] private Task StartWorkloadAsync(GuardianWorkloadDto? workload) => ApplyActionAsync("start", workload ?? SelectedWorkload);
    [RelayCommand] private Task StopWorkloadAsync(GuardianWorkloadDto? workload) => ApplyActionAsync("stop", workload ?? SelectedWorkload);
    [RelayCommand] private Task RestartWorkloadAsync(GuardianWorkloadDto? workload) => ApplyActionAsync("restart", workload ?? SelectedWorkload);
    [RelayCommand]
    private async Task DeleteWorkloadAsync(GuardianWorkloadDto? workload)
    {
        workload ??= SelectedWorkload; if (workload is null) return; IsLoading = true;
        try
        {
            var result = await client.DeleteAsync(workload.Id);
            SetStatus(result.Success ? Message("guardian.delete.succeeded", workload.Name) : Message("guardian.delete.failed", ProblemText(result.ProblemCode)));
            if (!result.Success) await ShowPrivilegedHelperUnavailableAsyncIfNeeded(result.ProblemCode);
            if (result.Success) ClearDefinition();
        }
        catch (Exception exception) { SetStatus(Message("guardian.delete.failed", exception.Message)); }
        finally { IsLoading = false; }
        await RefreshAsync();
    }
    [RelayCommand] private async Task OpenLogsAsync(GuardianWorkloadDto? workload)
    {
        workload ??= SelectedWorkload;
        if (workload is not null && ShowLogsAsync is not null) await ShowLogsAsync(workload);
    }
    private bool HasSelectedWorkload => SelectedWorkload is not null && !IsLoading;
    partial void OnSelectedWorkloadChanged(GuardianWorkloadDto? value)
    {
        NotifyActionCommands();
        if (value is not null && !_suppressSelectionDefinitionLoad) _ = LoadDefinitionAsync(value.Id);
    }
    partial void OnIsLoadingChanged(bool value) => NotifyActionCommands();
    private void NotifyActionCommands() { }
    private async Task ApplyActionAsync(string action, GuardianWorkloadDto? workload)
    {
        if (workload is null) return; IsLoading = true;
        try
        {
            var result = await client.ApplyActionAsync(workload.Id, action);
            SetStatus(result.Success ? Message("guardian.action.succeeded", action, workload.Name) : Message("guardian.action.failed", action, ProblemText(result.ProblemCode)));
            if (!result.Success) await ShowPrivilegedHelperUnavailableAsyncIfNeeded(result.ProblemCode);
        }
        catch (Exception exception) { SetStatus(Message("guardian.action.failed", action, exception.Message)); }
        finally { IsLoading = false; }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task CreateWorkloadAsync()
    {
        if (string.IsNullOrWhiteSpace(DefinitionName) || string.IsNullOrWhiteSpace(ExecutablePath) || string.IsNullOrWhiteSpace(WorkingDirectory) || string.IsNullOrWhiteSpace(RunAs))
        {
            SetStatus(Message("guardian.validation.required"));
            return;
        }
        var saved = false;
        IsLoading = true;
        try
        {
            var definition = GuardianDefinitionEditor.Merge(_loadedDefinition, DefinitionId, DefinitionName,
                ExecutablePath, ArgumentsText, WorkingDirectory, EnabledOnBoot, RunAs,
                isWindows());
            var result = RequiresAdministratorApproval()
                ? await (SaveWithAdministratorApprovalAsync?.Invoke(definition) ?? Task.FromResult<GuardianAgentResponse?>(null))
                : await client.UpsertAsync(definition);
            if (result is null) return;
            SetStatus(result.Success ? Message("guardian.create.succeeded", DefinitionName) : Message("guardian.create.failed", ProblemText(result.ProblemCode)));
            if (!result.Success) await ShowPrivilegedHelperUnavailableAsyncIfNeeded(result.ProblemCode);
            saved = result.Success;
        }
        catch (Exception exception) { SetStatus(Message("guardian.create.failed", exception.Message)); }
        finally
        {
            IsLoading = false;
        }
        if (!saved) return;
        await RefreshAsync();
        if (CloseEditorAsync is not null) await CloseEditorAsync();
    }

    private string ProblemText(string? problemCode) => helperProblem(problemCode) ?? (string.IsNullOrWhiteSpace(problemCode) ? "unknown error" : problemCode);

    private Task ShowPrivilegedHelperUnavailableAsyncIfNeeded(string? problemCode) =>
        helperProblem(problemCode) is not null
            ? ShowPrivilegedHelperUnavailableAsync?.Invoke(problemCode) ?? Task.CompletedTask
            : Task.CompletedTask;

    /// <summary>
    /// Loads a definition only if it is still the current editor target. Selection changes
    /// and opening a new workload invalidate older requests, preventing late responses from
    /// replacing the text currently being edited.
    /// </summary>
    private async Task<bool> LoadDefinitionAsync(string workloadId)
    {
        var loadVersion = Interlocked.Increment(ref _definitionLoadVersion);
        try
        {
            var result = await client.GetDefinitionAsync(workloadId);
            if (loadVersion != Volatile.Read(ref _definitionLoadVersion)
                || !result.Success
                || result.Definition is null
                || SelectedWorkload?.Id != workloadId)
                return false;
            var definition = result.Definition;
            _loadedDefinition = definition;
            DefinitionId = definition.Id;
            DefinitionName = definition.Name;
            ExecutablePath = definition.ExecutablePath;
            WorkingDirectory = definition.WorkingDirectory;
            ArgumentsText = string.Join(Environment.NewLine, definition.Arguments);
            EnabledOnBoot = definition.EnabledOnBoot;
            RunAs = definition.RunAs ?? string.Empty;
            return true;
        }
        catch
        {
            // Do not open an empty editor when the Agent did not return a definition.
            return false;
        }
    }

    private void ClearDefinition()
    {
        Interlocked.Increment(ref _definitionLoadVersion);
        _loadedDefinition = null;
        // The ID is the immutable key used by routes, persistence, and audit records.
        // It is deliberately generated by the client and never exposed for editing.
        DefinitionId = Guid.NewGuid().ToString("N");
        DefinitionName = ExecutablePath = WorkingDirectory = ArgumentsText = string.Empty;
        RunAs = username() ?? string.Empty;
        EnabledOnBoot = false;
        SelectedWorkload = null;
    }

    private bool RequiresAdministratorApproval() => isWindows()
        ? !string.Equals(username()?.Trim(), RunAs.Trim(), StringComparison.OrdinalIgnoreCase)
        : !string.Equals(username()?.Trim(), RunAs.Trim(), StringComparison.Ordinal);
}
