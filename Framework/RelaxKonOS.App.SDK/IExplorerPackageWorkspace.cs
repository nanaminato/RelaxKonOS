using Avalonia.Controls;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.AppSDK;

/// <summary>Desktop workspace supplied to the official Explorer package.</summary>
public interface IExplorerPackageWorkspace
{
    ManagedWindow? Open(string? initialPath, Func<string, Control, ManagedWindow> showWindow);
    bool TryNavigate(ManagedWindow window, string path);
    string Localize(string key);
}

public interface IExplorerPackageContext
{
    IExplorerPackageWorkspace? ExplorerWorkspace { get; }
}
