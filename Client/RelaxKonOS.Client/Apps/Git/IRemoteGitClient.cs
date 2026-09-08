using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git;

/// <summary>Typed JWT client for the server's Git repository facade.</summary>
public interface IRemoteGitClient
{
    // ── Host Git engine probe & install ──
    Task<GitEngineStatusDto> GetEngineStatusAsync(CancellationToken cancellationToken = default);
    Task<GitEngineInstallResult> InstallEngineAsync(CancellationToken cancellationToken = default);

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
    Task<GitOperationResult> FetchAsync(string id, CancellationToken cancellationToken = default);
    Task<GitOperationResult> PullAsync(string id, GitPullRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> PushAsync(string id, GitPushRequest? request = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitCommitDto>> GetLogAsync(string id, int limit = 200, int skip = 0,
        GitLogQuery? query = null, CancellationToken cancellationToken = default);
    Task<GitCommitDetailDto> GetCommitDetailAsync(string id, string sha, CancellationToken cancellationToken = default);
    Task<GitDiffDto> GetDiffAsync(string id, string path, bool staged = false, string? @ref = null, CancellationToken cancellationToken = default);
    Task<GitOperationResult> RevertAsync(string id, GitRevertRequest request, CancellationToken cancellationToken = default);
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
