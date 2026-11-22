using System.ComponentModel;
using System.Windows.Input;

namespace RelaxKonOS.AppSDK;

public interface IServerModuleReview : INotifyPropertyChanged
{
    Func<string, string> Labels { get; }
    ISystemLanguage Language { get; }
    bool HasStatus { get; }
    string StatusText { get; }
}

public interface IServerModulePlanReview : IServerModuleReview
{
    bool Busy { get; }
    bool CanApply { get; }
    bool CanApplyPackagePlan { get; }
    bool HasPackagePlan { get; }
    bool HasPackagePlanPrivileges { get; }
    bool HasPlan { get; }
    bool HasPlanBlockers { get; }
    string PackagePlanDigestText { get; }
    string PackagePlanExpiresText { get; }
    string PackagePlanModuleText { get; }
    string PackagePlanPrivilegesText { get; }
    string PackagePlanPublisherText { get; }
    string PackagePlanReleaseText { get; }
    string PackagePlanRuntimeText { get; }
    string PackagePlanSummary { get; }
    string PackagePlanTitle { get; }
    string PackagePlanVersionText { get; }
    string PlanActionText { get; }
    string PlanAddedText { get; }
    string PlanBlockedText { get; }
    string PlanExpiresText { get; }
    bool PlanIsRemoval { get; }
    string PlanRemovedText { get; }
    string PlanRestartText { get; }
    string PlanServiceText { get; }
    string PlanTargetText { get; }
    Task ApplyPlanAsync();
    Task ApplyPackagePlanAsync();
    ICommand DiscardPlanCommand { get; }
    ICommand DiscardPackagePlanCommand { get; }
}
