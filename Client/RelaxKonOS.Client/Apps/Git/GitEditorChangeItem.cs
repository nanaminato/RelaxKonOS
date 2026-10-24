using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git;

/// <summary>Index/worktree entries keep separate comparisons but share a commit-path selection.</summary>
public sealed record GitEditorChangeItem(GitFileChangeDto Change, GitFileChangeItem? Selection)
{
    public bool CanSelect => Selection is not null;
}
