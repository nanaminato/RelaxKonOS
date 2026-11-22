using System.ComponentModel;
using System.Windows.Input;

namespace RelaxKonOS.App.Settings.Views;

public interface IPersonalizationLayoutPage
{
    IPersonalizationLayout Editor { get; }
}

public interface IPersonalizationLayout : INotifyPropertyChanged
{
    Func<string, string> Labels { get; }
    IReadOnlyList<PersonalizationOption> ShellChoices { get; }
    string SelectedShellId { get; set; }
    ICommand ResetLayoutCommand { get; }
}
