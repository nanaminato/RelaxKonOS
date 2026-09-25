using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.ServerCenter.Views;

internal partial class SshFileDeleteDialog : Window
{
    public SshFileDeleteDialog(int count)
    {
        InitializeComponent();
        Title = LocalizedText.Get("ssh_files.confirm_delete", "Confirm delete");
        PromptText.Text = string.Format(LocalizedText.Get("ssh_files.delete_prompt",
            "Permanently delete {0} selected items and their contents?"), count);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Delete_Click(object? sender, RoutedEventArgs e) => Close(true);
}
