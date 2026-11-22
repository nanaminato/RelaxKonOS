using Avalonia.Controls;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.AppSDK;

public interface IServerCenterPackageWorkspace
{
    void Open(Func<string, Control, ManagedWindow> showWindow);
}

public interface IServerCenterPackageContext
{
    IServerCenterPackageWorkspace? ServerCenterWorkspace { get; }
}
