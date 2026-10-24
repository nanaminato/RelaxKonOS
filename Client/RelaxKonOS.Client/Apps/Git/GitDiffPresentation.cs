using RelaxKonOS.Client.Controls.Diff;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git;

/// <summary>
/// Everything a diff surface needs to render: the parsed patch, the title, the change counts and
/// the notice a patch that has no text needs instead. Shared by the modal dialog and the log page's
/// inline pane so both show the same thing without a second implementation.
/// </summary>
public sealed class GitDiffPresentation
{
    public GitDiffPresentation(GitDiffDto diff)
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
