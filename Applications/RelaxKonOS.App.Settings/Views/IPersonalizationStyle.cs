using System.ComponentModel;
using System.Windows.Input;

namespace RelaxKonOS.App.Settings.Views;

public interface IPersonalizationStylePage
{
    IPersonalizationStyle Editor { get; }
}

public interface IPersonalizationStyle : INotifyPropertyChanged
{
    Func<string, string> Labels { get; }
    IReadOnlyList<PersonalizationOption> SystemStyleChoices { get; }
    string SelectedSystemStyleId { get; set; }
    string? SystemStyleProblem { get; }
    bool HasSystemStyleProblem { get; }
    bool IsUsingRecommendedStyle { get; }
    ICommand ApplyRecommendedStyleCommand { get; }
    ICommand ResetStyleCommand { get; }
}
