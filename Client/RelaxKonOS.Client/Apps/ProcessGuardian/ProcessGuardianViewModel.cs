using System.Collections.ObjectModel;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ProcessGuardian;

namespace RelaxKonOS.Client.Apps.ProcessGuardian;

public sealed partial class ProcessGuardianViewModel(IProcessGuardianClient client, IAuthSession session) : ObservableObject
{
    // The Guardian Agent accepts one local pipe connection at a time. Keep UI refreshes
    // serialized too, otherwise concurrent status/list requests can time out and replace
    // a freshly saved workload list with an empty response.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private int _activeRefreshes;
    private long _definitionLoadVersion;
    private bool _suppressSelectionDefinitionLoad;
    public ObservableCollection<GuardianWorkloadDto> Workloads { get; } = [];
    [ObservableProperty] private GuardianWorkloadDto? _selectedWorkload;
    [ObservableProperty] private string _statusText = LocalizedText.Get("guardian.status.loading");
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _definitionId = string.Empty;
    [ObservableProperty] private string _definitionName = string.Empty;
    [ObservableProperty] private string _executablePath = string.Empty;
    [ObservableProperty] private string _workingDirectory = string.Empty;
    [ObservableProperty] private string _argumentsText = string.Empty;
    [ObservableProperty] private string _runAs = string.Empty;
    [ObservableProperty] private bool _enabledOnBoot;

    public Func<bool, Task>? ShowEditorAsync { get; set; }
    public Func<GuardianWorkloadDto, Task>? ShowLogsAsync { get; set; }
    public Func<Task>? CloseEditorAsync { get; set; }
    /// <summary>Provided by the window to surface unavailable privileged operations prominently.</summary>
    public Func<string?, Task>? ShowPrivilegedHelperUnavailableAsync { get; set; }
    /// <summary>Provided by the window and called only for a cross-account RunAs change.</summary>
    public Func<Task<RunAsAdministratorApproval?>>? RequestAdministratorApprovalAsync { get; set; }

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
            var workloads = await client.ListWorkloadsAsync();
            StatusText = status.IsInstalled
                ? LocalizedText.Format("guardian.status.available", status.Version ?? "")
                : PrivilegedHelperProblemText.TryFormat(status.ProblemCode, out var helperMessage)
                    ? helperMessage
                    : status.ProblemCode is "guardian.agent_not_configured" or "guardian.agent_not_installed"
                    ? LocalizedText.Get("guardian.status.install_required")
                    : LocalizedText.Format("guardian.status.unavailable", status.ProblemCode);
            if (!status.IsInstalled) await ShowPrivilegedHelperUnavailableAsyncIfNeeded(status.ProblemCode);
            Workloads.Clear(); foreach (var workload in workloads) Workloads.Add(workload);
        }
        catch (Exception exception)
        {
            StatusText = LocalizedText.Format("guardian.status.failed", exception.Message);
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
            StatusText = LocalizedText.Get("guardian.definition_load_failed");
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
            StatusText = result.Success ? LocalizedText.Format("guardian.delete.succeeded", workload.Name) : LocalizedText.Format("guardian.delete.failed", ProblemText(result.ProblemCode));
            if (!result.Success) await ShowPrivilegedHelperUnavailableAsyncIfNeeded(result.ProblemCode);
            if (result.Success) ClearDefinition();
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("guardian.delete.failed", exception.Message); }
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
            StatusText = result.Success ? LocalizedText.Format("guardian.action.succeeded", action, workload.Name) : LocalizedText.Format("guardian.action.failed", action, ProblemText(result.ProblemCode));
            if (!result.Success) await ShowPrivilegedHelperUnavailableAsyncIfNeeded(result.ProblemCode);
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("guardian.action.failed", action, exception.Message); }
        finally { IsLoading = false; }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task CreateWorkloadAsync()
    {
        if (string.IsNullOrWhiteSpace(DefinitionName) || string.IsNullOrWhiteSpace(ExecutablePath) || string.IsNullOrWhiteSpace(WorkingDirectory) || string.IsNullOrWhiteSpace(RunAs))
        {
            StatusText = LocalizedText.Get("guardian.validation.required");
            return;
        }
        var saved = false;
        RunAsAdministratorApproval? approval = null;
        if (RequiresAdministratorApproval())
        {
            approval = await (RequestAdministratorApprovalAsync?.Invoke() ?? Task.FromResult<RunAsAdministratorApproval?>(null));
            if (approval is null) return;
        }
        IsLoading = true;
        try
        {
            var arguments = ArgumentsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var definition = new ProcessDefinitionDto(DefinitionId.Trim(), DefinitionName.Trim(), ExecutablePath.Trim(), arguments, WorkingDirectory.Trim(), EnabledOnBoot, RunAs: RunAs.Trim());
            var result = await client.UpsertAsync(new UpsertGuardianWorkloadRequest(definition, approval));
            StatusText = result.Success ? LocalizedText.Format("guardian.create.succeeded", DefinitionName) : LocalizedText.Format("guardian.create.failed", ProblemText(result.ProblemCode));
            if (!result.Success) await ShowPrivilegedHelperUnavailableAsyncIfNeeded(result.ProblemCode);
            saved = result.Success;
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("guardian.create.failed", exception.Message); }
        finally
        {
            IsLoading = false;
        }
        if (!saved) return;
        await RefreshAsync();
        if (CloseEditorAsync is not null) await CloseEditorAsync();
    }

    private static string ProblemText(string? problemCode) => PrivilegedHelperProblemText.FormatOrFallback(problemCode, "unknown error");

    private Task ShowPrivilegedHelperUnavailableAsyncIfNeeded(string? problemCode) =>
        PrivilegedHelperProblemText.TryFormat(problemCode, out _)
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
        // The ID is the immutable key used by routes, persistence, and audit records.
        // It is deliberately generated by the client and never exposed for editing.
        DefinitionId = Guid.NewGuid().ToString("N");
        DefinitionName = ExecutablePath = WorkingDirectory = ArgumentsText = string.Empty;
        RunAs = session.CurrentUser?.Username ?? string.Empty;
        EnabledOnBoot = false;
        SelectedWorkload = null;
    }

    private bool RequiresAdministratorApproval() => session.CurrentServer?.Platform == PlatformKind.Windows
        ? !string.Equals(session.CurrentUser?.Username?.Trim(), RunAs.Trim(), StringComparison.OrdinalIgnoreCase)
        : !string.Equals(session.CurrentUser?.Username?.Trim(), RunAs.Trim(), StringComparison.Ordinal);
}
