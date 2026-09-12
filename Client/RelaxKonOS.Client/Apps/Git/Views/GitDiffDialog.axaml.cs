using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.Git.Views;

/// <summary>Native, read-only unified-diff viewer with hunk and line-number colouring.</summary>
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

internal sealed class GitDiffDialogModel
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

        if (HasTextPatch)
            foreach (var line in GitDiffLine.Parse(diff.Patch)) Lines.Add(line);
    }

    public ObservableCollection<GitDiffLine> Lines { get; } = [];
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

/// <summary>A parsed unified-diff line, retaining old/new line numbers for a compact review view.</summary>
public sealed class GitDiffLine
{
    private static readonly IBrush ContextBackground = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
    private static readonly IBrush AddedBackground = new SolidColorBrush(Color.FromArgb(42, 22, 163, 74));
    private static readonly IBrush RemovedBackground = new SolidColorBrush(Color.FromArgb(42, 220, 38, 38));
    private static readonly IBrush HunkBackground = new SolidColorBrush(Color.FromArgb(42, 37, 99, 235));
    private static readonly IBrush MetadataBackground = new SolidColorBrush(Color.FromArgb(28, 107, 114, 128));

    private GitDiffLine(string oldLine, string newLine, string marker, string text, IBrush background, IBrush foreground)
        => (OldLine, NewLine, Marker, Text, Background, Foreground) = (oldLine, newLine, marker, text, background, foreground);

    public string OldLine { get; }
    public string NewLine { get; }
    public string Marker { get; }
    public string Text { get; }
    public IBrush Background { get; }
    public IBrush Foreground { get; }

    public static IEnumerable<GitDiffLine> Parse(string patch)
    {
        var oldLine = 0;
        var newLine = 0;
        var inHunk = false;
        var sourceLines = patch.Split('\n');
        for (var index = 0; index < sourceLines.Length; index++)
        {
            if (index == sourceLines.Length - 1 && sourceLines[index].Length == 0) continue;
            var sourceLine = sourceLines[index];
            var line = sourceLine.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                (oldLine, newLine) = ParseHunkStart(line);
                inHunk = true;
                yield return Meta(line, HunkBackground);
                continue;
            }

            if (inHunk && line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                yield return new GitDiffLine(string.Empty, (newLine++).ToString(), "+", line[1..], AddedBackground, ThemeBrushes.Get("SuccessBrush"));
                continue;
            }
            if (inHunk && line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))
            {
                yield return new GitDiffLine((oldLine++).ToString(), string.Empty, "−", line[1..], RemovedBackground, ThemeBrushes.Get("DangerBrush"));
                continue;
            }
            if (inHunk && line.StartsWith(' '))
            {
                yield return new GitDiffLine((oldLine++).ToString(), (newLine++).ToString(), " ", line[1..], ContextBackground, ThemeBrushes.Get("TextPrimaryBrush"));
                continue;
            }

            yield return Meta(line, MetadataBackground);
        }
    }

    private static GitDiffLine Meta(string line, IBrush background)
        => new(string.Empty, string.Empty, string.Empty, line, background, ThemeBrushes.Get("TextSecondaryBrush"));

    private static (int Old, int New) ParseHunkStart(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (fields.Length > 1 ? ParseRange(fields[1]) : 0, fields.Length > 2 ? ParseRange(fields[2]) : 0);
    }

    private static int ParseRange(string value)
    {
        var number = value.TrimStart('-', '+').Split(',')[0];
        return int.TryParse(number, out var parsed) ? parsed : 0;
    }
}
