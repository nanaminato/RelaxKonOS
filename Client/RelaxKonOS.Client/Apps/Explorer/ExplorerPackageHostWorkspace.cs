using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Explorer;

internal sealed class ExplorerPackageHostWorkspace(IServiceProvider services) : IExplorerPackageWorkspace
{
    private readonly ExplorerHostWorkspace _workspace = new();

    public ManagedWindow? Open(string? initialPath, Func<string, Control, ManagedWindow> showWindow) =>
        _workspace.Open(new AppContext(new AppId("relaxkonos.explorer"), services.GetRequiredService<IWindowManager>(), services),
            initialPath, showWindow);

    public bool TryNavigate(ManagedWindow window, string path) => _workspace.TryNavigate(window, path);

    public string Localize(string key) => LocalizedText.Get(key);
}
