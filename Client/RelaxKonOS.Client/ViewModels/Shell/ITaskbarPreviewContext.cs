using System.ComponentModel;
using System.Windows.Input;

namespace RelaxKonOS.Client.ViewModels.Shell;

/// <summary>The preview strip's presentation state and permitted window commands.</summary>
internal interface ITaskbarPreviewContext : INotifyPropertyChanged
{
    TaskbarGroupViewModel? OpenTaskbarGroup { get; }
    bool IsStartOpen { get; }
    void ShowTaskbarPreview(TaskbarGroupViewModel group);
    ICommand ActivateTaskbarWindowCommand { get; }
    ICommand CloseTaskbarWindowCommand { get; }
    ICommand CloseTaskbarPreviewCommand { get; }
}
