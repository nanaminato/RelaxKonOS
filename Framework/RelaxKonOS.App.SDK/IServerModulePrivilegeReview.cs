using System.Windows.Input;

namespace RelaxKonOS.AppSDK;

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
