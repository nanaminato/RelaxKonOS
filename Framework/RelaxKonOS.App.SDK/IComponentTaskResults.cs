using System.Windows.Input;

namespace RelaxKonOS.AppSDK;

public interface IComponentTaskResults
{
    bool ShowTasks { get; }
    bool HasTaskHistory { get; }
    IEnumerable<IComponentTaskHistoryItem> TaskHistory { get; }
    IComponentTaskHistoryItem? SelectedTask { get; set; }
    bool CanSelectTask { get; }
    ICommand ReloadTaskHistoryCommand { get; }
    string TaskState { get; }
    bool ShowTaskResult { get; }
    string TaskResultSummary { get; }
    string TaskResultIdentity { get; }
    IComponentTaskOperationProgress Runtime { get; }
    bool ShowTaskDomainProgress { get; }
    string TaskNotice { get; }
    ICommand ReadTaskEvidenceCommand { get; }
    bool CanReadTaskEvidence { get; }
    ICommand RefreshTaskCommand { get; }
    bool CanRefreshTask { get; }
    ICommand ResumeTaskCommand { get; }
    bool CanResumeTask { get; }
    ICommand CancelTaskCommand { get; }
    bool CanCancelTask { get; }
    IEnumerable<IComponentTaskEvidenceItem> TaskEvidence { get; }
    bool IsTaskBusy { get; }
    ICommand RecheckTaskRecoveryCommand { get; }
    bool CanRecheckTaskRecovery { get; }
    bool HasTaskRecovery { get; }
    string TaskRecoverySummary { get; }
    string RecoveryServiceState { get; }
    string RecoveryVersions { get; }
    ICommand ReadTaskRecoveryCommand { get; }
    bool CanReadTaskRecovery { get; }
    ICommand RetryTaskCompensationCommand { get; }
    bool CanRetryTaskCompensation { get; }
    ICommand StopTaskRecoveryCommand { get; }
    bool CanStopTaskRecovery { get; }
    ICommand CloseTaskRecoveryCommand { get; }
    bool CanCloseTaskRecovery { get; }
    IEnumerable<IComponentRecoveryAttemptItem> RecoveryAttempts { get; }
    IEnumerable<IComponentTaskStepItem> TaskSteps { get; }
}

public interface IComponentTaskHistoryItem
{
    string Title { get; }
}

public interface IComponentTaskEvidenceItem
{
    string Kind { get; }
    string Operation { get; }
    string DomainState { get; }
    string Observation { get; }
    string Receipt { get; }
    string Commit { get; }
}

public interface IComponentRecoveryAttemptItem
{
    string Action { get; }
    string Result { get; }
    string Identity { get; }
}

public interface IComponentTaskStepItem
{
    string Title { get; }
    string State { get; }
    string Operation { get; }
    string DataImpact { get; }
    string Problem { get; }
    bool HasOperation { get; }
    bool HasDataImpact { get; }
    bool HasProblem { get; }
}

public interface IComponentTaskOperationProgress
{
    string OperationText { get; }
    int OperationProgress { get; }
    bool IsOperationIndeterminate { get; }
}
