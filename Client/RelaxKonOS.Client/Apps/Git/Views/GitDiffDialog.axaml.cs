using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.App.Git;
using RelaxKonOS.App.Git.Views;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.Git.Views;

/// <summary>Modal shell: a <see cref="GitDiffPane"/> in a window, for diffs opened outside the log page.</summary>
internal partial class GitDiffDialog : UserControl
{
    private readonly ModalDialog<bool> _dialog;

    public GitDiffDialog(GitDiffDto diff, ModalDialog<bool> dialog)
    {
        _dialog = dialog;
        InitializeComponent();
        DataContext = new GitDiffPresentation(diff, LocalizedText.Get);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => _dialog.Close(true);
}
