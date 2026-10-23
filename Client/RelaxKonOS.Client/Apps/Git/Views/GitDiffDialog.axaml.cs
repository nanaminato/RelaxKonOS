using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Client.Controls.Diff;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.Git.Views;

/// <summary>Modal shell around <see cref="DiffView"/>: path, change counts and the notices a patch cannot show as text.</summary>
internal partial class GitDiffDialog : UserControl
{
    private readonly ModalDialog<bool> _dialog;

    public GitDiffDialog(GitDiffDto diff, ModalDialog<bool> dialog)
    {
        _dialog = dialog;
        InitializeComponent();
        DataContext = new GitDiffDialogModel(diff);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => _dialog.Close(true);
}

public sealed class GitDiffDialogModel
{
    public GitDiffDialogModel(GitDiffDto diff)
    {
        DisplayPath = string.IsNullOrWhiteSpace(diff.OldPath) ? diff.Path : $"{diff.OldPath} → {diff.Path}";
        AdditionsText = $"+{diff.Additions}";
        DeletionsText = $"−{diff.Deletions}";
        IsBinary = diff.Binary;
        IsTruncated = diff.Truncated;
        HasTextPatch = !diff.Binary && !string.IsNullOrEmpty(diff.Patch);
        IsEmpty = !diff.Binary && string.IsNullOrEmpty(diff.Patch);
        Description = diff.Binary
            ? LocalizedText.Get("git.dialog.diff.binary_description")
            : LocalizedText.Get("git.dialog.diff.description");
        BinaryMessage = LocalizedText.Get("git.dialog.diff.binary_message");
        EmptyMessage = LocalizedText.Get("git.dialog.diff.empty_message");
        TruncatedMessage = LocalizedText.Get("git.dialog.diff.truncated_message");
        CloseText = LocalizedText.Get("git.dialog.diff.close");
        Document = HasTextPatch ? DiffDocument.Parse(diff.Patch) : DiffDocument.Parse(null);
    }

    /// <summary>The patch, parsed once and shared by the gutter, the bands and the ink.</summary>
    public DiffDocument Document { get; }

    public string DisplayPath { get; }
    public string Description { get; }
    public string AdditionsText { get; }
    public string DeletionsText { get; }
    public string BinaryMessage { get; }
    public string EmptyMessage { get; }
    public string TruncatedMessage { get; }
    public string CloseText { get; }
    public bool IsBinary { get; }
    public bool IsTruncated { get; }
    public bool IsEmpty { get; }
    public bool HasTextPatch { get; }
}
