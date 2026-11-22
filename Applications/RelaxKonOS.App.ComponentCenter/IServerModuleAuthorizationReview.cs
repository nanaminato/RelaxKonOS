using System.Windows.Input;
using RelaxKonOS.ModuleHost.Protocol;

namespace RelaxKonOS.App.ComponentCenter;

public interface IServerModulePrivilegeReview : IServerModuleReview
{
    bool HasPrivilegePlan { get; }
    string PrivilegePlanId { get; set; }
    string PrivilegeText { get; }
    string PrivilegeAdministrator { get; set; }
    string PrivilegePassword { get; set; }
    ICommand LoadPrivilegeCommand { get; }
    ICommand ApprovePrivilegeCommand { get; }
    ICommand CancelPrivilegeCommand { get; }
}

public interface IServerModuleConsentReview : IServerModuleReview
{
    bool CanManage { get; }
    bool CanPrepareConsent { get; }
    bool ShowConsentContentSha256 { get; }
    string ConsentContentSha256 { get; set; }
    string ConsentCountText { get; }
    decimal ConsentHours { get; set; }
    ServerModuleConsentKindChoice ConsentKind { get; set; }
    IReadOnlyList<ServerModuleConsentKindChoice> ConsentKinds { get; }
    string ConsentModuleText { get; }
    string ConsentReason { get; set; }
    IReadOnlyList<ModulePrivilegeConsent> Consents { get; }
    ModulePrivilegeConsent? SelectedConsent { get; set; }
    string ConsentTarget { get; set; }
    string ConsentText { get; }
    ICommand PrepareConsentCommand { get; }
    ICommand ApproveConsentCommand { get; }
    ICommand RefreshConsentsCommand { get; }
    ICommand RevokeConsentCommand { get; }
}

public sealed record ServerModuleConsentKindChoice(string Kind, string Action, string LabelKey, Func<string, string> Localize)
{
    public string Label => Localize(LabelKey);
}
