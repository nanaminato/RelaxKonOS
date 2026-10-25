using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Apps.FileServices;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public sealed record ComponentTaskHistoryItem(ComponentTaskReference Reference)
{
    public LocalizedStatus Title => LocalizedStatus.Format("component_center.task_reference", Reference.Plan.CreatedAt.LocalDateTime);
}
public sealed record ComponentTaskStepItem(TaskStep Plan, TaskStepRun Run)
{
    public LocalizedStatus Title => new ComponentInspectionStep(Plan).Title;
    public LocalizedStatus State => LocalizedStatus.Key("component_center.task_step." + Run.State);
}

public partial class ComponentCenterViewModel
{
    private readonly ITaskExecutionClient? _execution;
    private readonly ComponentTaskReferenceStore? _references;
    private TaskPlanApproval? _taskApproval;
    private bool _driveTask, _taskDisposed, _referenceReady;
    private string _approvalKey = Guid.NewGuid().ToString("N");
    public Func<TaskPlan, Task<bool>>? ConfirmTaskAsync { get; set; }
    public Func<Task<HostAdministratorCredentials?>>? TaskCredentialsAsync { get; set; }
    public ObservableCollection<ComponentTaskHistoryItem> TaskHistory { get; } = [];
    public ObservableCollection<ComponentTaskStepItem> TaskSteps { get; } = [];
    [ObservableProperty] private ComponentTaskHistoryItem? selectedTask;
    [ObservableProperty] private TaskRun? currentTaskRun;
    [ObservableProperty] private bool isTaskBusy;
    [ObservableProperty] private LocalizedStatus taskNotice = LocalizedStatus.Literal("");
    public bool CanSelectTask => !IsTaskBusy;
    public bool ShowTasks => _execution is not null;
    public bool CanApproveTask => !_taskDisposed && _execution is not null && _references is not null && _referenceReady && !IsTaskBusy && !IsInspecting
        && Runtime.CanManage && !Runtime.IsBusy && !Runtime.IsInvalidated && FrozenChangePlan is { } plan
        && plan.Semantics.Steps.All(step => step.Disposition is TaskStepDisposition.Ready or TaskStepDisposition.NoOp);
    public bool CanRefreshTask => _execution is not null && SelectedTask is not null && !IsTaskBusy && !_taskDisposed && !Runtime.IsInvalidated;
    public bool CanResumeTask => CanRefreshTask && Runtime.CanManage && (CurrentTaskRun?.State is TaskRunState.Queued or TaskRunState.Running
        || CurrentTaskRun is null && _taskApproval?.State == TaskApprovalState.Granted);
    public bool CanCancelTask => CanRefreshTask && Runtime.CanManage && CurrentTaskRun?.State is TaskRunState.Queued or TaskRunState.Running or TaskRunState.Cancelling;
    public LocalizedStatus TaskState => LocalizedStatus.Key(CurrentTaskRun is { } run ? "component_center.task_state." + run.State : "component_center.task_unchecked");
    public bool ShowTaskDomainProgress => CurrentTaskRun?.Steps.Any(step => step.DomainOperationId is { } id && id == Runtime.Installation?.PendingOperationId) == true;
    partial void OnIsTaskBusyChanged(bool value) { TaskProperties(); OnPropertyChanged(nameof(CanPreview)); OnPropertyChanged(nameof(CanPlanDraft)); OnPropertyChanged(nameof(CanInspect)); RefreshTree(); }
    partial void OnFrozenChangePlanChanged(TaskPlan? value) { _approvalKey = Guid.NewGuid().ToString("N"); TaskProperties(); }
    partial void OnCurrentTaskRunChanged(TaskRun? value)
    {
        TaskSteps.Clear();
        if (value is not null && SelectedTask is { } selected)
            foreach (var step in value.Steps) TaskSteps.Add(new(selected.Reference.Plan.Semantics.Steps.Single(item => item.StepId == step.StepId), step));
        TaskProperties();
    }
    partial void OnSelectedTaskChanged(ComponentTaskHistoryItem? value)
    { _driveTask = false; _taskApproval = null; CurrentTaskRun = null; TaskProperties(); }
    private void TaskProperties()
    {
        foreach (var property in new[] { nameof(CanApproveTask), nameof(CanRefreshTask), nameof(CanResumeTask), nameof(CanCancelTask), nameof(CanSelectTask), nameof(TaskState), nameof(ShowTaskDomainProgress) }) OnPropertyChanged(property);
    }
    [RelayCommand] private void ReloadTaskHistory()
    {
        if (IsTaskBusy || _references is null) return;
        try
        {
            var entries = _references.Read(_server, _actor); TaskHistory.Clear();
            foreach (var entry in entries) TaskHistory.Add(new(entry));
            SelectedTask = TaskHistory.FirstOrDefault(); _referenceReady = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _referenceReady = false; TaskNotice = LocalizedStatus.Key("component_center.task_reference_failed"); }
        TaskProperties();
    }
    private void Track(ComponentTaskReference reference)
    {
        try { _references!.Save(_server, _actor, reference); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new TaskReferencePersistenceException(ex); }
        var previous = TaskHistory.SingleOrDefault(item => item.Reference.ApprovalId == reference.ApprovalId);
        if (previous is not null) TaskHistory.Remove(previous);
        var item = new ComponentTaskHistoryItem(reference); TaskHistory.Insert(0, item); SelectedTask = item;
    }
    [RelayCommand] private async Task ApproveTaskAsync()
    {
        if (!CanApproveTask || FrozenChangePlan is not { } plan || ConfirmTaskAsync is null) return;
        var revision = _draftRevision; var generation = _inspectionGeneration; var approvalKey = _approvalKey;
        IsTaskBusy = true;
        try
        {
            if (!await ConfirmTaskAsync(plan) || _taskDisposed || revision != _draftRevision || generation != _inspectionGeneration || Runtime.IsInvalidated) return;
            TaskPlanApproval approval;
            try { approval = await _execution!.ApproveAsync(plan, new() { PlanDigest = plan.PlanDigest, Confirmed = true }, approvalKey, _inspectionLifetime.Token); }
            catch (HttpRequestException ex) when (ex.Message == "task.administrator_authentication_failed")
            {
                var credentials = TaskCredentialsAsync is { } prompt ? await prompt() : null;
                if (credentials is null || _taskDisposed || revision != _draftRevision || generation != _inspectionGeneration || Runtime.IsInvalidated) return;
                approval = await _execution!.ApproveAsync(plan, new() { PlanDigest = plan.PlanDigest, Confirmed = true,
                    AdministratorUsername = credentials.Username, Password = credentials.Password }, approvalKey, _inspectionLifetime.Token);
            }
            if (_taskDisposed || revision != _draftRevision || generation != _inspectionGeneration || Runtime.IsInvalidated) return;
            var reference = new ComponentTaskReference(plan, approval.ApprovalId, Guid.NewGuid().ToString("N"), null);
            Track(reference); // Must be durable before starting, including when the start response is lost.
            _taskApproval = approval;
            var run = await _execution!.StartAsync(plan, approval, reference.StartKey, _inspectionLifetime.Token);
            Track(reference with { RunId = run.RunId }); CurrentTaskRun = run;
            _driveTask = !_taskDisposed && revision == _draftRevision && generation == _inspectionGeneration && !Runtime.IsInvalidated;
            if (_driveTask) { Drafts.Clear(); Persist(); }
            TaskNotice = LocalizedStatus.Key("component_center.task_accepted");
        }
        catch (Exception ex) when (TaskFailure(ex)) { TaskError(ex); }
        finally { IsTaskBusy = false; }
    }
    [RelayCommand] private async Task RefreshTaskAsync()
    {
        if (!CanRefreshTask || SelectedTask is not { } selected) return;
        IsTaskBusy = true;
        try
        {
            var reference = selected.Reference;
            if (reference.RunId is not { } runId)
            {
                _taskApproval = await _execution!.ReadApprovalAsync(reference.Plan, reference.ApprovalId, _inspectionLifetime.Token);
                if (_taskApproval.RunId is not { } accepted)
                { TaskNotice = LocalizedStatus.Key(_taskApproval.State == TaskApprovalState.Granted ? "component_center.task_not_started" : "component_center.task_approval_unavailable"); return; }
                reference = reference with { RunId = accepted }; Track(reference); runId = accepted;
            }
            CurrentTaskRun = CurrentTaskRun is { } previous
                ? await _execution!.RefreshAsync(reference.Plan, previous, _inspectionLifetime.Token)
                : await _execution!.ReadAsync(reference.Plan, runId, _inspectionLifetime.Token);
            TaskNotice = LocalizedStatus.Literal("");
        }
        catch (Exception ex) when (TaskFailure(ex)) { TaskError(ex); }
        finally { IsTaskBusy = false; }
    }
    [RelayCommand] private async Task ResumeTaskAsync()
    {
        if (!CanResumeTask || SelectedTask is not { } selected || ConfirmTaskAsync is null) return;
        IsTaskBusy = true;
        try
        {
            if (!await ConfirmTaskAsync(selected.Reference.Plan) || _taskDisposed || Runtime.IsInvalidated) return;
            if (CurrentTaskRun is null && _taskApproval is { } approval)
            {
                var run = await _execution!.StartAsync(selected.Reference.Plan, approval, selected.Reference.StartKey, _inspectionLifetime.Token);
                Track(selected.Reference with { RunId = run.RunId }); CurrentTaskRun = run;
            }
            _driveTask = true; TaskNotice = LocalizedStatus.Key("component_center.task_accepted");
        }
        catch (Exception ex) when (TaskFailure(ex)) { TaskError(ex); }
        finally { IsTaskBusy = false; }
    }
    [RelayCommand] private async Task CancelTaskAsync()
    {
        if (!CanCancelTask || CurrentTaskRun is not { } run || SelectedTask is not { } selected) return;
        IsTaskBusy = true;
        try { CurrentTaskRun = await _execution!.CancelAsync(selected.Reference.Plan, run, _inspectionLifetime.Token); }
        catch (Exception ex) when (TaskFailure(ex)) { TaskError(ex); }
        finally { IsTaskBusy = false; }
    }
    public async Task TickTaskAsync()
    {
        if (!CanRefreshTask || SelectedTask is not { } selected) return;
        if (CurrentTaskRun is not { } run) { await RefreshTaskAsync(); return; }
        if (run.State is not (TaskRunState.Queued or TaskRunState.Running or TaskRunState.Cancelling)) return;
        if (!_driveTask && !run.CancellationRequested) { await RefreshTaskAsync(); return; }
        IsTaskBusy = true;
        try { CurrentTaskRun = await _execution!.AdvanceAsync(selected.Reference.Plan, run, _inspectionLifetime.Token); }
        catch (Exception ex) when (TaskFailure(ex)) { TaskError(ex); }
        finally { IsTaskBusy = false; }
    }
    private static bool TaskFailure(Exception ex) => ex is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException;
    private void TaskError(Exception ex)
    {
        _driveTask = false;
        if (ex is TaskReferencePersistenceException) _referenceReady = false;
        if (!_taskDisposed) TaskNotice = LocalizedStatus.Key(ex is TaskReferencePersistenceException
            ? "component_center.task_reference_failed" : "component_center.task_request_failed");
    }
    private sealed class TaskReferencePersistenceException(Exception cause) : IOException("task.reference_failed", cause);
}
