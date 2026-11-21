using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Runtime;

namespace RelaxKonOS.Client.Apps.Welcome;

public partial class WelcomeViewModel : ObservableObject
{
    [RelayCommand]
    private void OpenNotepad()
    {
        var applications = App.Services.GetRequiredService<ApplicationManager>();
        if (!applications.Launch(new AppId("relaxkonos.notepad")))
            applications.Launch(new AppId("relaxkonos.appinstaller"));
    }
}
