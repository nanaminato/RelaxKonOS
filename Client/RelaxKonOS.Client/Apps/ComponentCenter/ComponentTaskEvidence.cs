using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public sealed record ComponentTaskEvidenceItem(TaskStepResultEvidence Evidence)
{
    public LocalizedStatus Kind => LocalizedStatus.Key(Evidence.RecoveryCheck ? "component_center.evidence_recovery" : "component_center.evidence_original");
    public LocalizedStatus Operation => LocalizedStatus.Format("component_center.result_operation", Evidence.OperationId.ToString("D"));
    public LocalizedStatus DomainState => LocalizedStatus.Key("component_center.evidence_domain." + Evidence.OperationState);
    public LocalizedStatus Observation => Evidence.Snapshot is not { } snapshot ? LocalizedStatus.Key("component_center.evidence_missing")
        : LocalizedStatus.Format("component_center.evidence_observation", snapshot.Installation.InstallationId.ToString("D"), snapshot.Installation.StateRevision,
            snapshot.MeasuredVersion ?? "—", snapshot.Installation.ActiveReleaseId ?? "—");
    public LocalizedStatus Receipt => LocalizedStatus.Key("component_center.evidence_receipt." + (Evidence.Snapshot?.Receipt?.Outcome.ToString() ?? "Unknown"));
    public LocalizedStatus Commit => Evidence.Snapshot?.CommitWitness is not { } witness
        ? LocalizedStatus.Key("component_center.evidence_commit_missing")
        : LocalizedStatus.Format("component_center.evidence_commit", witness.Installation.StateRevision,
            witness.MeasuredVersion ?? "—", witness.Installation.ActiveReleaseId ?? "—");
}

public partial class ComponentCenterViewModel
{
    public ObservableCollection<ComponentTaskEvidenceItem> TaskEvidence { get; } = [];
    private TaskRun? _evidenceRun;
    public bool CanReadTaskEvidence => CanRefreshTask && CurrentTaskRun is not null;
    private void ClearTaskEvidence() { _evidenceRun = null; TaskEvidence.Clear(); }

    [RelayCommand] private async Task ReadTaskEvidenceAsync()
    {
        Runtime.SynchronizeAccess();
        if (!CanReadTaskEvidence || SelectedTask is not { } selected || CurrentTaskRun is not { } run) return;
        IsTaskBusy = true; ClearTaskEvidence();
        try
        {
            var evidence = await _execution!.ReadEvidenceAsync(selected.Reference.Plan, run, _inspectionLifetime.Token);
            Runtime.SynchronizeAccess();
            if (_taskDisposed || Runtime.IsInvalidated || !Runtime.CanRead || SelectedTask != selected || CurrentTaskRun != run) return;
            CurrentTaskRun = evidence.Run;
            _evidenceRun = evidence.Run;
            foreach (var observation in evidence.Observations) TaskEvidence.Add(new(observation));
            TaskNotice = LocalizedStatus.Key(evidence.Observations.IsEmpty ? "component_center.evidence_empty" : "component_center.evidence_history_hint");
        }
        catch (Exception ex) when (TaskFailure(ex)) { TaskError(ex); }
        finally { IsTaskBusy = false; }
    }
}
