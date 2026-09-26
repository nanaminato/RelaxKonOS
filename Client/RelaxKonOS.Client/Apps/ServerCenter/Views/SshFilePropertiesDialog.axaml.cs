using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.ServerCenter.Views;

internal partial class SshFilePropertiesDialog : Window
{
    public SshFilePropertiesDialog(SshFileEntry entry)
    {
        InitializeComponent();
        Title = LocalizedText.Get("ssh_files.properties", "Properties");
        NameText.Text = entry.Name;
        PathText.Text = entry.Path;
        TypeText.Text = entry.Kind;
        SizeText.Text = entry.SizeText;
        ModifiedText.Text = entry.ModifiedText;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
