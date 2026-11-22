using Avalonia.Controls;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.AppSDK;

/// <summary>Workspace operations supplied to the official Component Center package.</summary>
public interface IComponentCenterWorkspace
{
    void Open(ComponentCenterViewFactories views, Func<string, Control, ManagedWindow> showWorkspace);
    bool CanNavigate(Uri uri);
    void Navigate(Uri uri);
}

public interface IComponentCenterPackageContext
{
    IComponentCenterWorkspace? ComponentCenterWorkspace { get; }
}

public sealed record ComponentCenterViewFactories(
    Func<ComponentCenterPresentation, Control> CreateMain,
    Func<Control> CreateModuleHeader,
    Func<Control> CreateModuleStore,
    Func<Control> CreateModuleSecurity,
    Func<ModalDialog<bool>, Control> CreateModulePlan,
    Func<ModalDialog<bool>, Control> CreateModulePolicy,
    Func<ModalDialog<bool>, Control> CreateModulePrivilege,
    Func<ModalDialog<bool>, Control> CreateModuleConsent,
    Func<ModalDialog<bool>, TaskPlanConfirmationPresentation, Control> CreateTaskPlanConfirmation,
    Func<ComponentTaskDialogPresentation, Control> CreateTaskDialogLayout);

public sealed record TaskPlanConfirmationPresentation(
    TaskPlan Plan,
    Func<string, string> Label,
    Func<string, object?[], string> Format,
    Func<TaskStep, (string Title, string Effects)> DescribeStep);
