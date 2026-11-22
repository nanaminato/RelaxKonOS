using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.App.Settings.Views;

public sealed class LegalTextDialogViewModel(string title, string text, string closeLabel, Action close)
{
    public string Title { get; } = title;
    public string Text { get; } = text;
    public string CloseLabel { get; } = closeLabel;
    public IRelayCommand CloseCommand { get; } = new RelayCommand(close);
}
