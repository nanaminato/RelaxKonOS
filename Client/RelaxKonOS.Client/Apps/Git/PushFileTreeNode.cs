using System.Collections.ObjectModel;

namespace RelaxKonOS.Client.Apps.Git;

/// <summary>A node in the push-preview file tree. Folders are represented by non-file nodes.</summary>
public sealed class PushFileTreeNode
{
    public PushFileTreeNode(string name, bool isFile, string? status = null)
    {
        Name = name;
        IsFile = isFile;
        Status = status ?? string.Empty;
    }

    public string Name { get; }
    public bool IsFile { get; }
    public string Status { get; }
    public string Icon => IsFile ? "📄" : "📁";
    public ObservableCollection<PushFileTreeNode> Children { get; } = [];
}
