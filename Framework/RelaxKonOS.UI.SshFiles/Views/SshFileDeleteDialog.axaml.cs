using RelaxKonOS.AppSDK;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.UI.SshFiles;

internal partial class SshFileDeleteDialog : Window
{
    public SshLabels Labels { get; }

    public SshFileDeleteDialog(int count, ISystemLanguage language)
    {
        Labels = new SshLabels(language);
        InitializeComponent();
        Title = SshFilesLocalization.Get(language, "ssh_files.confirm_delete", "Confirm delete");
        PromptText.Text = string.Format(SshFilesLocalization.Get(language, "ssh_files.delete_prompt",
            "Permanently delete {0} selected items and their contents?"), count);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Delete_Click(object? sender, RoutedEventArgs e) => Close(true);
}
