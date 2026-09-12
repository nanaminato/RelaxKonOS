using System.Collections.ObjectModel;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git;

/// <summary>A node in the push-preview file tree. Folders are represented by non-file nodes.</summary>
public sealed class PushFileTreeNode
{
    public PushFileTreeNode(string name, bool isFile, string? status = null, GitFileChangeDto? file = null, string? commitSha = null)
    {
        Name = name;
        IsFile = isFile;
        Status = status ?? string.Empty;
        File = file;
        CommitSha = commitSha;
    }

    public string Name { get; }
    public bool IsFile { get; }
    public string Status { get; }
    /// <summary>The backing change for a leaf node; null for folders.</summary>
    public GitFileChangeDto? File { get; }
    /// <summary>Commit to use for aggregate push previews; null for a selected-commit preview or folders.</summary>
    public string? CommitSha { get; }
    public string Icon => IsFile ? "📄" : "📁";
    public ObservableCollection<PushFileTreeNode> Children { get; } = [];
}
