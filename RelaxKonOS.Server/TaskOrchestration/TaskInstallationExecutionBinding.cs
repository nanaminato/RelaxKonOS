using RelaxKonOS.Server.Installations;

namespace RelaxKonOS.Server.TaskOrchestration;

/// <summary>Created by the task dispatcher only; never decoded from installation HTTP options.</summary>
public sealed class TaskInstallationExecutionBinding
{
    public InstallationTaskOwner Owner { get; }
    private readonly Func<Guid, TaskStepExecutionAuthorization> _bind;
    private readonly Action _validateAcceptance;
    internal TaskInstallationExecutionBinding(InstallationTaskOwner owner, Action validateAcceptance, Func<Guid, TaskStepExecutionAuthorization> bind)
    { Owner = owner; _bind = bind; _validateAcceptance = validateAcceptance; }
    internal void ValidateAcceptance() => _validateAcceptance();
    internal IInstallationProgress Attach(IInstallationProgress progress) => new TaskProgress(progress, _bind(progress.OperationId));
    private sealed class TaskProgress(IInstallationProgress inner, TaskStepExecutionAuthorization authority) : ITaskInstallationProgress
    {
        public TaskStepExecutionAuthorization Authority => authority;
        public Guid OperationId => inner.OperationId;
        public Task ReportAsync(InstallationProgress progress, CancellationToken cancellationToken = default) => inner.ReportAsync(progress, cancellationToken);
    }
}
public interface ITaskInstallationProgress : IInstallationProgress
{
    TaskStepExecutionAuthorization Authority { get; }
}
