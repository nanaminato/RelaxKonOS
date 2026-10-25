using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.TaskOrchestration;
using System.Collections.ObjectModel;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public sealed record ComponentRecoveryAttemptItem(TaskRecoveryAttempt Attempt)
{
    public LocalizedStatus Action => LocalizedStatus.Key("component_center.task_recovery_action." + Attempt.Action);
    public LocalizedStatus Result => LocalizedStatus.Key(!Attempt.ResponseRecorded ? "component_center.recovery_attempt_unknown"
        : Attempt.ProblemCode is null ? "component_center.recovery_attempt_recorded" : "component_center.recovery_attempt_failed");
    public string Identity => Attempt.AttemptId.ToString("D");
}

public partial class ComponentCenterViewModel
{
    public ObservableCollection<ComponentRecoveryAttemptItem> RecoveryAttempts { get; } = [];
    [ObservableProperty] private TaskRecoveryView? taskRecovery;
    private readonly Dictionary<(Guid Run, TaskRecoveryAction Action, long Revision), string> _recoveryKeys = [];
    public Func<TaskRecoveryAction, TaskRecoveryView, Task<bool>>? ConfirmTaskRecoveryAsync { get; set; }
    public bool HasTaskRecovery => CurrentTaskRun?.State == TaskRunState.NeedsRecovery;
    public bool CanReadTaskRecovery => CanRefreshTask && HasTaskRecovery;
    public bool CanRetryTaskCompensation => CanReadTaskRecovery && Runtime.CanManage && TaskRecovery is { Recovery.Required: true } view
        && view.Run.RunId == CurrentTaskRun!.RunId && view.Run.Revision == CurrentTaskRun.Revision
        && view.Recovery.OperationId is { } operation && CurrentTaskRun.Steps.Any(step =>
            step.State == TaskStepRunState.NeedsRecovery && step.DomainOperationId == operation);
    public bool CanStopTaskRecovery => CanRetryTaskCompensation && TaskRecovery?.Recovery.Service?.Running == true;
    public bool CanCloseTaskRecovery => CanRetryTaskCompensation && TaskRecovery is { Recovery.NeedsOperatorDecision: true, Recovery.Service.Running: false, Recovery.ServiceProblem: null };
    public LocalizedStatus TaskRecoverySummary => TaskRecovery is { } view
        ? LocalizedStatus.Format("component_center.task_recovery_summary", view.Recovery.OperationId?.ToString("D") ?? "—", view.Attempts.Length)
        : LocalizedStatus.Key("component_center.task_recovery_read_hint");
    public LocalizedStatus RecoveryServiceState => LocalizedStatus.Key(TaskRecovery?.Recovery is not { ServiceProblem: null, Service: { } service }
        ? "component_center.recovery_service_unknown" : service.Running ? "component_center.recovery_service_running" : "component_center.recovery_service_stopped");
    public LocalizedStatus RecoveryVersions => LocalizedStatus.Format("component_center.recovery_versions",
        TaskRecovery?.Recovery.PreviousReleaseId ?? "—", TaskRecovery?.Recovery.TargetReleaseId ?? "—");
    partial void OnTaskRecoveryChanged(TaskRecoveryView? value)
    {
        RecoveryAttempts.Clear();
        if (value is not null) foreach (var attempt in value.Attempts.Reverse()) RecoveryAttempts.Add(new(attempt));
        RecoveryProperties();
    }
    private void RecoveryProperties()
    {
        foreach (var property in new[] { nameof(HasTaskRecovery), nameof(CanReadTaskRecovery), nameof(CanRetryTaskCompensation),
            nameof(CanStopTaskRecovery), nameof(CanCloseTaskRecovery), nameof(TaskRecoverySummary), nameof(RecoveryServiceState), nameof(RecoveryVersions) }) OnPropertyChanged(property);
    }
    private async Task LoadTaskRecoveryAsync(TaskPlan plan, TaskRun run)
    {
        if (_taskDisposed || Runtime.IsInvalidated || !Runtime.CanRead) return;
        var selected = SelectedTask;
        var view = await _execution!.ReadRecoveryAsync(plan, run, _inspectionLifetime.Token);
        Runtime.SynchronizeAccess();
        if (_taskDisposed || Runtime.IsInvalidated || !Runtime.CanRead || SelectedTask != selected || CurrentTaskRun != run) return;
        CurrentTaskRun = view.Run; TaskRecovery = view;
    }
    [RelayCommand] private async Task ReadTaskRecoveryAsync()
    {
        Runtime.SynchronizeAccess();
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
        Runtime.SynchronizeAccess();
        if (!(action switch { TaskRecoveryAction.RetryCompensation => CanRetryTaskCompensation, TaskRecoveryAction.EmergencyStop => CanStopTaskRecovery,
            TaskRecoveryAction.Close => CanCloseTaskRecovery, _ => false }) || SelectedTask is not { } selected || CurrentTaskRun is not { } run
            || TaskRecovery is not { Recovery.OperationId: { } operationId } view || ConfirmTaskRecoveryAsync is null) return;
        var keyScope = (run.RunId, action, run.Revision);
        if (!_recoveryKeys.TryGetValue(keyScope, out var key)) _recoveryKeys[keyScope] = key = Guid.NewGuid().ToString("N");
        IsTaskBusy = true; _driveTask = false;
        bool Current()
        {
            Runtime.SynchronizeAccess();
            return !_taskDisposed && !Runtime.IsInvalidated && Runtime.CanRead && Runtime.CanManage
                && SelectedTask?.Reference.RunId == selected.Reference.RunId && CurrentTaskRun == run;
        }
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
            if (!Current()) return;
            CurrentTaskRun = response.Run; TaskRecovery = null;
            TaskNotice = LocalizedStatus.Key(response.Attempt.ResponseRecorded ? "component_center.task_recovery_response" : "component_center.task_recovery_response_unknown");
            await LoadTaskRecoveryAsync(selected.Reference.Plan, response.Run);
        }
        catch (Exception ex) when (TaskFailure(ex)) { TaskRecovery = null; TaskError(ex); }
        finally { IsTaskBusy = false; }
    }
}
