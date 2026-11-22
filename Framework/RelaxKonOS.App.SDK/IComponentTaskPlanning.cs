using System.Windows.Input;

namespace RelaxKonOS.AppSDK;

public interface IComponentTaskPlanning : IComponentTaskDialogModel
{
    IEnumerable<IComponentTaskSelection> TaskWorkloads { get; }
    IComponentTaskSelection? TaskWorkload { get; set; }
    ICommand AddTaskWorkloadCommand { get; }
    string Notice { get; }
    IEnumerable<IComponentTaskSelection> BatchComponents { get; }
    bool ShowInspection { get; }
    ICommand InspectCommand { get; }
    bool CanInspect { get; }
    bool IsInspecting { get; }
    string InspectionNotice { get; }
    IEnumerable<IComponentInspectionStep> InspectionSteps { get; }
    bool ShowBatchPlanPanel { get; }
    ICommand ApproveTaskCommand { get; }
    bool CanApproveTask { get; }
    string ChangePlanNotice { get; }
    string ChangePlanDigest { get; }
    IEnumerable<IComponentInspectionStep> ChangePlanSteps { get; }
}

public interface IComponentTaskSelection
{
    string Label { get; }
    bool? IsChecked { get; set; }
    bool CanSelect { get; }
}

public interface IComponentInspectionStep
{
    string Title { get; }
    string State { get; }
    string Effects { get; }
    string Problems { get; }
}
