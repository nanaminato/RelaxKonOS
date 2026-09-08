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
        App.Services.GetRequiredService<ApplicationManager>()
            .Launch(new AppId("remoteos.notepad"));
    }
}
