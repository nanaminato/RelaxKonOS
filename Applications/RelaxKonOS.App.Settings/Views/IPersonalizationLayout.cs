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
    IReadOnlyList<DesktopLayoutOption> ShellChoices { get; }
    string SelectedShellId { get; set; }
    ICommand ResetLayoutCommand { get; }
}

public sealed record DesktopLayoutOption(string Id, string SelectionDisplayName, string? UnavailableReason)
{
    public bool HasUnavailableReason => !string.IsNullOrWhiteSpace(UnavailableReason);
}
