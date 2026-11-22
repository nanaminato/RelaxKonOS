using System.Windows.Input;

namespace RelaxKonOS.AppSDK;

public interface IServerModuleSecurity
{
    bool CanManage { get; }
    bool HealthToneOk { get; }
    bool HealthToneBad { get; }
    bool HasHealthReport { get; }
    string HealthSummaryText { get; }
    string HealthCheckedText { get; }
    IEnumerable<IServerModuleHealthRow> HealthRows { get; }
    string PrivilegeStateText { get; }
    string ConsentCountText { get; }
    ICommand CheckHealthCommand { get; }
    ICommand OpenPrivilegeReviewCommand { get; }
    ICommand OpenConsentEditorCommand { get; }
}

public interface IServerModuleHealthRow
{
    string ModuleId { get; }
    string ProblemText { get; }
    bool HasProblem { get; }
    string VersionText { get; }
    bool ToneOk { get; }
    bool ToneBad { get; }
    string StatusText { get; }
}
