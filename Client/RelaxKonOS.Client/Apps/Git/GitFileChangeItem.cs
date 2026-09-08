using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git;

/// <summary>Wrapper for GitFileChangeDto that adds selection state for UI binding.</summary>
public partial class GitFileChangeItem : ObservableObject
{
    public GitFileChangeDto File { get; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Raised when IsSelected changes so the ViewModel can update aggregate properties.</summary>
    public event EventHandler? SelectionChanged;

    public GitFileChangeItem(GitFileChangeDto file, bool isSelected = false)
    {
        File = file;
        IsSelected = isSelected;
    }

    public string Path => File.Path;
    public string Status => File.Status;

    /// <summary>Gets a status icon character for display.</summary>
    public string StatusIcon => Status switch
    {
        "modified" => "M",
        "added" => "A",
        "deleted" => "D",
        "renamed" => "R",
        "copied" => "C",
        "untracked" => "?",
        "conflicted" => "!",
        _ => " "
    };

    partial void OnIsSelectedChanged(bool value)
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
