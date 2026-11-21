using RelaxKonOS.UI.TextEditing.Diff;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.App.Git;

/// <summary>
/// Everything a diff surface needs to render: the parsed patch, the title, the change counts and
/// the notice a patch that has no text needs instead. Shared by the modal dialog and the log page's
/// inline pane so both show the same thing without a second implementation.
/// </summary>
public sealed class GitDiffPresentation
{
    public GitDiffPresentation(GitDiffDto diff, Func<string, string> localize)
    {
        DisplayPath = string.IsNullOrWhiteSpace(diff.OldPath) ? diff.Path : $"{diff.OldPath} → {diff.Path}";
        AdditionsText = $"+{diff.Additions}";
        DeletionsText = $"−{diff.Deletions}";
        IsBinary = diff.Binary;
        IsTruncated = diff.Truncated;
        HasTextPatch = !diff.Binary && !string.IsNullOrEmpty(diff.Patch);
        IsEmpty = !diff.Binary && string.IsNullOrEmpty(diff.Patch);
        Description = diff.Binary
            ? localize("git.dialog.diff.binary_description")
            : localize("git.dialog.diff.description");
        BinaryMessage = localize("git.dialog.diff.binary_message");
        EmptyMessage = localize("git.dialog.diff.empty_message");
        TruncatedMessage = localize("git.dialog.diff.truncated_message");
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

    public bool IsBinary { get; }

    public bool IsTruncated { get; }

    public bool IsEmpty { get; }

    public bool HasTextPatch { get; }
}
