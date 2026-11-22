using Avalonia.Controls;
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
    Func<Control> CreateModuleSecurity,
    Func<ModalDialog<bool>, Control> CreateModulePlan,
    Func<ModalDialog<bool>, Control> CreateModulePolicy,
    Func<ModalDialog<bool>, Control> CreateModulePrivilege);
