using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public partial class ComponentCenterViewModel
{
    [ObservableProperty] private TaskRecoveryView? taskRecovery;
    private readonly Dictionary<(Guid Run, TaskRecoveryAction Action, long Revision), string> _recoveryKeys = [];
    public Func<TaskRecoveryAction, TaskRecoveryView, Task<bool>>? ConfirmTaskRecoveryAsync { get; set; }
    public bool HasTaskRecovery => CurrentTaskRun?.State == TaskRunState.NeedsRecovery;
    public bool CanReadTaskRecovery => CanRefreshTask && HasTaskRecovery;
    public bool CanRetryTaskCompensation => CanReadTaskRecovery && Runtime.CanManage && TaskRecovery is { Recovery.Required: true } view
        && view.Run.RunId == CurrentTaskRun!.RunId && view.Run.Revision == CurrentTaskRun.Revision;
    public bool CanStopTaskRecovery => CanRetryTaskCompensation && TaskRecovery?.Recovery.Service?.Running == true;
    public bool CanCloseTaskRecovery => CanRetryTaskCompensation && TaskRecovery is { Recovery.NeedsOperatorDecision: true, Recovery.Service.Running: false, Recovery.ServiceProblem: null };
    public LocalizedStatus TaskRecoverySummary => TaskRecovery is { } view
        ? LocalizedStatus.Format("component_center.task_recovery_summary", view.Recovery.OperationId?.ToString("D") ?? "—", view.Attempts.Length)
        : LocalizedStatus.Key("component_center.task_recovery_read_hint");
    partial void OnTaskRecoveryChanged(TaskRecoveryView? value) => RecoveryProperties();
    private void RecoveryProperties()
    {
        foreach (var property in new[] { nameof(HasTaskRecovery), nameof(CanReadTaskRecovery), nameof(CanRetryTaskCompensation),
            nameof(CanStopTaskRecovery), nameof(CanCloseTaskRecovery), nameof(TaskRecoverySummary) }) OnPropertyChanged(property);
    }
    private async Task LoadTaskRecoveryAsync(TaskPlan plan, TaskRun run)
    {
        if (_taskDisposed || Runtime.IsInvalidated || !Runtime.CanRead) return;
        var view = await _execution!.ReadRecoveryAsync(plan, run, _inspectionLifetime.Token);
        if (_taskDisposed || Runtime.IsInvalidated || !Runtime.CanRead) return;
        CurrentTaskRun = view.Run; TaskRecovery = view;
    }
    [RelayCommand] private async Task ReadTaskRecoveryAsync()
    {
        if (!CanReadTaskRecovery || SelectedTask is not { } selected || CurrentTaskRun is not { } run) return;
        IsTaskBusy = true; TaskRecovery = null;
        try { await LoadTaskRecoveryAsync(selected.Reference.Plan, run); }
        catch (Exception ex) when (TaskFailure(ex)) { TaskError(ex); }
        finally { IsTaskBusy = false; }
    }
    [RelayCommand] private Task RetryTaskCompensationAsync() => RecoverTaskAsync(TaskRecoveryAction.RetryCompensation);
    [RelayCommand] private Task StopTaskRecoveryAsync() => RecoverTaskAsync(TaskRecoveryAction.EmergencyStop);
    [RelayCommand] private Task CloseTaskRecoveryAsync() => RecoverTaskAsync(TaskRecoveryAction.Close);
    private async Task RecoverTaskAsync(TaskRecoveryAction action)
    {
        if (!(action switch { TaskRecoveryAction.RetryCompensation => CanRetryTaskCompensation, TaskRecoveryAction.EmergencyStop => CanStopTaskRecovery,
            TaskRecoveryAction.Close => CanCloseTaskRecovery, _ => false }) || SelectedTask is not { } selected || CurrentTaskRun is not { } run
            || TaskRecovery is not { Recovery.OperationId: { } operationId } view || ConfirmTaskRecoveryAsync is null) return;
        var keyScope = (run.RunId, action, run.Revision);
        if (!_recoveryKeys.TryGetValue(keyScope, out var key)) _recoveryKeys[keyScope] = key = Guid.NewGuid().ToString("N");
        IsTaskBusy = true; _driveTask = false;
        bool Current() => !_taskDisposed && !Runtime.IsInvalidated && Runtime.CanRead && Runtime.CanManage
            && SelectedTask?.Reference.RunId == selected.Reference.RunId && CurrentTaskRun == run;
        try
        {
            if (!await ConfirmTaskRecoveryAsync(action, view) || !Current()) return;
            TaskRecoveryRequest Request(string? username = null, string? password = null) => new()
            { ExpectedRevision = run.Revision, OperationId = operationId, Action = action, Confirmed = true, AdministratorUsername = username, Password = password };
            TaskRecoveryResponse response;
            try { response = await _execution!.RecoverAsync(selected.Reference.Plan, run, Request(), key, _inspectionLifetime.Token); }
            catch (HttpRequestException ex) when (ex.Message == "task.administrator_authentication_failed")
            {
                var credentials = TaskCredentialsAsync is { } prompt ? await prompt() : null;
                if (credentials is null || !Current()) return;
                response = await _execution!.RecoverAsync(selected.Reference.Plan, run, Request(credentials.Username, credentials.Password), key, _inspectionLifetime.Token);
            }
            _recoveryKeys.Remove(keyScope);
            if (_taskDisposed || Runtime.IsInvalidated) return;
            CurrentTaskRun = response.Run; TaskRecovery = null;
            TaskNotice = LocalizedStatus.Key(response.Attempt.ResponseRecorded ? "component_center.task_recovery_response" : "component_center.task_recovery_response_unknown");
            await LoadTaskRecoveryAsync(selected.Reference.Plan, response.Run);
        }
        catch (Exception ex) when (TaskFailure(ex)) { TaskRecovery = null; TaskError(ex); }
        finally { IsTaskBusy = false; }
    }
}
