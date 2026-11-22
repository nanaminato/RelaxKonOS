using RelaxKonOS.App.Settings.Views;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.Settings;

public interface ISettingsWindow
{
    void Open(ISettingsDialogs dialogs, Func<ISettingsWorkspace, ManagedWindow> showWorkspace);
    void Navigate(string route);
    void OpenEnvironment();
    void OpenApplicationPermissions(string appId);
}
