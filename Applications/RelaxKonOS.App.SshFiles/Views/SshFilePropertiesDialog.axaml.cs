using RelaxKonOS.AppSDK;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.App.SshFiles.Views;

internal partial class SshFilePropertiesDialog : Window
{
    public SshLabels Labels { get; }

    public SshFilePropertiesDialog(SshFileEntry entry, ISystemLanguage language)
    {
        Labels = new SshLabels(language);
        InitializeComponent();
        Title = SshFilesLocalization.Get(language, "ssh_files.properties", "Properties");
        NameText.Text = entry.Name;
        PathText.Text = entry.Path;
        TypeText.Text = entry.Kind;
        SizeText.Text = entry.SizeText;
        ModifiedText.Text = entry.ModifiedText;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
