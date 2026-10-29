using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git;

/// <summary>Typed JWT client for the server's Git repository facade.</summary>
public interface IRemoteGitClient
{
    Task<IReadOnlyList<GitGraphCommitDto>> GetGraphAsync(string id, string? reference = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitConsoleEntryDto>> GetConsoleAsync(string id, long after = 0, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitTagDto>> ListTagsAsync(string id, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CreateTagAsync(string id, GitTagCreateRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> RebaseAsync(string id, GitReferenceRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CherryPickAsync(string id, GitReferenceRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitStashDto>> ListStashesAsync(string id, CancellationToken cancellationToken = default);
    Task<GitOperationResult> StashAsync(string id, GitStashRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitWorktreeDto>> ListWorktreesAsync(string id, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CreateWorktreeAsync(string id, GitWorktreeCreateRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> RemoveWorktreeAsync(string id, GitWorktreeRemoveRequest request, CancellationToken cancellationToken = default);
    Task<GitPatchDto> ExportPatchAsync(string id, GitPatchRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> ApplyPatchAsync(string id, GitPatchApplyRequest request, CancellationToken cancellationToken = default);
    Task<GitCloneResult> CloneAsync(GitCloneRequest request, CancellationToken cancellationToken = default);

    // ── Host Git engine probe & install ──
    Task<IReadOnlyList<GitCredentialAccountDto>> ListCredentialAccountsAsync(CancellationToken cancellationToken = default);
    Task<GitCredentialAccountDto> SaveCredentialAccountAsync(GitCredentialAccountRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteCredentialAccountAsync(string accountId, CancellationToken cancellationToken = default);

    Task<GitEngineStatusDto> GetEngineStatusAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitRepositoryDto>> ListRepositoriesAsync(CancellationToken cancellationToken = default);
    Task<GitRepositoryDto?> GetRepositoryAsync(string id, CancellationToken cancellationToken = default);
    Task<GitRepositoryDto> RegisterRepositoryAsync(GitRepositoryRegistration registration, CancellationToken cancellationToken = default);
    Task<bool> UnregisterRepositoryAsync(string id, CancellationToken cancellationToken = default);
    Task<GitStatusDto> GetStatusAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitBranchDto>> ListBranchesAsync(string id, CancellationToken cancellationToken = default);
    Task<GitBranchComparisonDto> CompareBranchAsync(string id, string name, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CreateBranchAsync(string id, GitBranchCreateRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> DeleteBranchAsync(string id, string name, CancellationToken cancellationToken = default);
    Task<GitOperationResult> RenameBranchAsync(string id, string name, GitBranchRenameRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> SetBranchTrackingAsync(string id, string name, GitBranchTrackingRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CheckoutAsync(string id, GitCheckoutRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CommitAsync(string id, GitCommitRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> MergeBranchAsync(string id, GitMergeRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> FetchAsync(string id, GitFetchRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> PullAsync(string id, GitPullRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> PushAsync(string id, GitPushRequest? request = null, CancellationToken cancellationToken = default);
    /// <summary>Commits the push to <paramref name="remote"/>/<paramref name="remoteBranch"/> would upload,
    /// including when that remote branch does not exist yet (it is then created by the push).</summary>
    Task<GitPushPreviewDto> GetPushPreviewAsync(string id, string? localBranch = null, string? remote = null,
        string? remoteBranch = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitCommitDto>> GetLogAsync(string id, int limit = 200, int skip = 0,
        GitLogQuery? query = null, CancellationToken cancellationToken = default);
    Task<GitCommitDetailDto> GetCommitDetailAsync(string id, string sha, CancellationToken cancellationToken = default);
    Task<GitDiffDto> GetDiffAsync(string id, string path, bool staged = false, string? @ref = null, CancellationToken cancellationToken = default);
    Task<GitOperationResult> RevertAsync(string id, GitRevertRequest request, CancellationToken cancellationToken = default);
    Task<GitConflictStateDto> GetConflictStateAsync(string id, CancellationToken cancellationToken = default);
    Task<GitConflictFileDto> GetConflictAsync(string id, string path, CancellationToken cancellationToken = default);
    Task<GitOperationResult> ConflictOperationAsync(string id, GitConflictOperationRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> ResolveConflictsAsync(string id, GitResolveRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> ResetAsync(string id, GitResetRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> RestoreAsync(string id, GitRestoreRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> StageAsync(string id, GitStageRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> UnstageAsync(string id, GitUnstageRequest request, CancellationToken cancellationToken = default);

    // ── 路径探测与初始化（不依赖已注册仓库）──
    Task<GitRepositoryProbeDto> ProbeRepositoryAsync(string path, CancellationToken cancellationToken = default);
    Task<GitOperationResult> InitRepositoryAsync(string path, CancellationToken cancellationToken = default);

    // ── 远程（remote）管理 ──
    Task<IReadOnlyList<GitRemoteDto>> ListRemotesAsync(string id, CancellationToken cancellationToken = default);
    Task<GitOperationResult> AddRemoteAsync(string id, GitRemoteRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> UpdateRemoteAsync(string id, string name, GitRemoteRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> RemoveRemoteAsync(string id, string name, CancellationToken cancellationToken = default);
}
