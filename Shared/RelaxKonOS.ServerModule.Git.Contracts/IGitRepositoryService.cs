using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Server.Git;

/// <summary>The only server boundary allowed to invoke the host's git CLI and manage repository registrations.</summary>
public interface IGitRepositoryService
{
    Task<IReadOnlyList<GitGraphCommitDto>> GetGraphAsync(Guid id, Guid userId, string? reference = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitConsoleEntryDto>> GetConsoleAsync(Guid id, Guid userId, long after = 0, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitTagDto>> ListTagsAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CreateTagAsync(Guid id, Guid userId, GitTagCreateRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> RebaseAsync(Guid id, Guid userId, GitReferenceRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CherryPickAsync(Guid id, Guid userId, GitReferenceRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitStashDto>> ListStashesAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    Task<GitOperationResult> StashAsync(Guid id, Guid userId, GitStashRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitWorktreeDto>> ListWorktreesAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CreateWorktreeAsync(Guid id, Guid userId, GitWorktreeCreateRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> RemoveWorktreeAsync(Guid id, Guid userId, GitWorktreeRemoveRequest request, CancellationToken cancellationToken = default);
    Task<GitPatchDto> ExportPatchAsync(Guid id, Guid userId, GitPatchRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> ApplyPatchAsync(Guid id, Guid userId, GitPatchApplyRequest request, CancellationToken cancellationToken = default);
    Task<GitCloneResult> CloneAsync(GitCloneRequest request, Guid userId, CancellationToken cancellationToken = default);

    // ── Host Git engine probe & install ──
    Task<IReadOnlyList<GitCredentialAccountDto>> ListCredentialAccountsAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<GitCredentialAccountDto> SaveCredentialAccountAsync(Guid userId, GitCredentialAccountRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteCredentialAccountAsync(Guid userId, string accountId, CancellationToken cancellationToken = default);

    Task<GitEngineStatusDto> GetEngineStatusAsync(CancellationToken cancellationToken = default);

    // ── Repository registration (persisted in SQLite, isolated by user) ──
    Task<IReadOnlyList<GitRepositoryDto>> ListRepositoriesAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<GitRepositoryDto?> GetRepositoryAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    Task<GitRepositoryDto> RegisterRepositoryAsync(GitRepositoryRegistration registration, Guid userId, CancellationToken cancellationToken = default);
    Task<bool> UnregisterRepositoryAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);

    // ── Real-time git operations (not persisted) ──
    Task<GitStatusDto> GetStatusAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitBranchDto>> ListBranchesAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    Task<GitBranchComparisonDto> CompareBranchAsync(Guid id, Guid userId, string name, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CreateBranchAsync(Guid id, Guid userId, GitBranchCreateRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> DeleteBranchAsync(Guid id, Guid userId, string name, CancellationToken cancellationToken = default);
    Task<GitOperationResult> RenameBranchAsync(Guid id, Guid userId, string name, GitBranchRenameRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> SetBranchTrackingAsync(Guid id, Guid userId, string name, GitBranchTrackingRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CheckoutAsync(Guid id, Guid userId, GitCheckoutRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CommitAsync(Guid id, Guid userId, GitCommitRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> MergeBranchAsync(Guid id, Guid userId, GitMergeRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> FetchAsync(Guid id, Guid userId, GitFetchRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> PullAsync(Guid id, Guid userId, GitPullRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> PushAsync(Guid id, Guid userId, GitPushRequest? request = null, CancellationToken cancellationToken = default);
    /// <summary>Commits a push to the given destination would actually upload. Works for a destination
    /// ref that does not exist yet, so "the remote has never seen this branch" is previewable and pushable.</summary>
    Task<GitPushPreviewDto> GetPushPreviewAsync(Guid id, Guid userId, string? localBranch = null,
        string? remote = null, string? remoteBranch = null, int limit = 200, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitCommitDto>> GetLogAsync(Guid id, Guid userId, int limit = 200, int skip = 0,
        GitLogQuery? query = null, CancellationToken cancellationToken = default);
    Task<GitCommitDetailDto> GetCommitDetailAsync(Guid id, Guid userId, string sha, CancellationToken cancellationToken = default);
    Task<GitDiffDto> GetDiffAsync(Guid id, Guid userId, string path, bool staged = false, string? @ref = null, CancellationToken cancellationToken = default);
    Task<GitOperationResult> RevertAsync(Guid id, Guid userId, GitRevertRequest request, CancellationToken cancellationToken = default);
    Task<GitConflictStateDto> GetConflictStateAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    Task<GitConflictFileDto> GetConflictAsync(Guid id, Guid userId, string path, CancellationToken cancellationToken = default);
    Task<GitOperationResult> ConflictOperationAsync(Guid id, Guid userId, GitConflictOperationRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> ResolveConflictsAsync(Guid id, Guid userId, GitResolveRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> ResetAsync(Guid id, Guid userId, GitResetRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> RestoreAsync(Guid id, Guid userId, GitRestoreRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> StageAsync(Guid id, Guid userId, GitStageRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> UnstageAsync(Guid id, Guid userId, GitUnstageRequest request, CancellationToken cancellationToken = default);

    // ── 路径探测与初始化（不依赖已注册仓库；用于"打开文件夹作为项目"流程）──
    Task<GitRepositoryProbeDto> ProbeRepositoryAsync(string path, Guid userId, CancellationToken cancellationToken = default);
    Task<GitOperationResult> InitRepositoryAsync(string path, Guid userId, CancellationToken cancellationToken = default);

    // ── 远程（remote）管理 ──
    Task<IReadOnlyList<GitRemoteDto>> ListRemotesAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    Task<GitOperationResult> AddRemoteAsync(Guid id, Guid userId, GitRemoteRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> UpdateRemoteAsync(Guid id, Guid userId, string name, GitRemoteRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> RemoveRemoteAsync(Guid id, Guid userId, string name, CancellationToken cancellationToken = default);
}
