using Avalonia.Controls;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.AppSDK;

/// <summary>Workspace operations supplied to the official Component Center package.</summary>
public interface IComponentCenterWorkspace
{
    void Open(Func<ComponentCenterPresentation, Control> createView, Func<string, Control, ManagedWindow> showWorkspace);
    bool CanNavigate(Uri uri);
    void Navigate(Uri uri);
}

public interface IComponentCenterPackageContext
{
    IComponentCenterWorkspace? ComponentCenterWorkspace { get; }
}
