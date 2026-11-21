using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Git;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerGitCapability(AppId appId, IAppPermissionManager permissions,
    ExternalServerConnection connection, IServerGit? client, CancellationToken lifetime) : IServerGit
{
    private IServerGit Client => client ?? throw new InvalidOperationException("The Git client is unavailable.");

    public Task<IReadOnlyList<GitGraphCommitDto>> GetGraphAsync(string id, string? reference, CancellationToken cancellationToken) =>
        ReadAsync(token => Client.GetGraphAsync(id, reference, token), cancellationToken);

    public Task<IReadOnlyList<GitConsoleEntryDto>> GetConsoleAsync(string id, long after, CancellationToken cancellationToken) =>
        ReadAsync(token => Client.GetConsoleAsync(id, after, token), cancellationToken);

    public Task<IReadOnlyList<GitTagDto>> ListTagsAsync(string id, CancellationToken cancellationToken) =>
        ReadAsync(token => Client.ListTagsAsync(id, token), cancellationToken);

    public Task<GitOperationResult> CreateTagAsync(string id, GitTagCreateRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.CreateTagAsync(id, request, token), cancellationToken);

    public Task<GitOperationResult> RebaseAsync(string id, GitReferenceRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.RebaseAsync(id, request, token), cancellationToken);

    public Task<GitOperationResult> CherryPickAsync(string id, GitReferenceRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.CherryPickAsync(id, request, token), cancellationToken);

    public Task<IReadOnlyList<GitStashDto>> ListStashesAsync(string id, CancellationToken cancellationToken) =>
        ReadAsync(token => Client.ListStashesAsync(id, token), cancellationToken);

    public Task<GitOperationResult> StashAsync(string id, GitStashRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.StashAsync(id, request, token), cancellationToken);

    public Task<IReadOnlyList<GitWorktreeDto>> ListWorktreesAsync(string id, CancellationToken cancellationToken) =>
        ReadAsync(token => Client.ListWorktreesAsync(id, token), cancellationToken);

    public Task<GitOperationResult> CreateWorktreeAsync(string id, GitWorktreeCreateRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.CreateWorktreeAsync(id, request, token), cancellationToken);

    public Task<GitOperationResult> RemoveWorktreeAsync(string id, GitWorktreeRemoveRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.RemoveWorktreeAsync(id, request, token), cancellationToken);

    public Task<GitPatchDto> ExportPatchAsync(string id, GitPatchRequest request, CancellationToken cancellationToken) =>
        ReadAsync(token => Client.ExportPatchAsync(id, request, token), cancellationToken);

    public Task<GitOperationResult> ApplyPatchAsync(string id, GitPatchApplyRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.ApplyPatchAsync(id, request, token), cancellationToken);

    public Task<GitCloneResult> CloneAsync(GitCloneRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.CloneAsync(request, token), cancellationToken);

    public Task<IReadOnlyList<GitCredentialAccountDto>> ListCredentialAccountsAsync(CancellationToken cancellationToken) =>
        ManageAsync(token => Client.ListCredentialAccountsAsync(token), cancellationToken);

    public Task<GitCredentialAccountDto> SaveCredentialAccountAsync(GitCredentialAccountRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.SaveCredentialAccountAsync(request, token), cancellationToken);

    public Task<bool> DeleteCredentialAccountAsync(string accountId, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.DeleteCredentialAccountAsync(accountId, token), cancellationToken);

    public Task<GitEngineStatusDto> GetEngineStatusAsync(CancellationToken cancellationToken) =>
        ReadAsync(token => Client.GetEngineStatusAsync(token), cancellationToken);

    public Task<IReadOnlyList<GitRepositoryDto>> ListRepositoriesAsync(CancellationToken cancellationToken) =>
        ReadAsync(token => Client.ListRepositoriesAsync(token), cancellationToken);

    public Task<GitRepositoryDto?> GetRepositoryAsync(string id, CancellationToken cancellationToken) =>
        ReadAsync(token => Client.GetRepositoryAsync(id, token), cancellationToken);

    public Task<GitRepositoryDto> RegisterRepositoryAsync(GitRepositoryRegistration registration, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.RegisterRepositoryAsync(registration, token), cancellationToken);

    public Task<bool> UnregisterRepositoryAsync(string id, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.UnregisterRepositoryAsync(id, token), cancellationToken);

    public Task<GitStatusDto> GetStatusAsync(string id, CancellationToken cancellationToken) =>
        ReadAsync(token => Client.GetStatusAsync(id, token), cancellationToken);

    public Task<IReadOnlyList<GitBranchDto>> ListBranchesAsync(string id, CancellationToken cancellationToken) =>
        ReadAsync(token => Client.ListBranchesAsync(id, token), cancellationToken);

    public Task<GitBranchComparisonDto> CompareBranchAsync(string id, string name, CancellationToken cancellationToken) =>
        ReadAsync(token => Client.CompareBranchAsync(id, name, token), cancellationToken);

    public Task<GitOperationResult> CreateBranchAsync(string id, GitBranchCreateRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.CreateBranchAsync(id, request, token), cancellationToken);

    public Task<GitOperationResult> DeleteBranchAsync(string id, string name, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.DeleteBranchAsync(id, name, token), cancellationToken);

    public Task<GitOperationResult> RenameBranchAsync(string id, string name, GitBranchRenameRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.RenameBranchAsync(id, name, request, token), cancellationToken);

    public Task<GitOperationResult> SetBranchTrackingAsync(string id, string name, GitBranchTrackingRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.SetBranchTrackingAsync(id, name, request, token), cancellationToken);

    public Task<GitOperationResult> CheckoutAsync(string id, GitCheckoutRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.CheckoutAsync(id, request, token), cancellationToken);

    public Task<GitOperationResult> CommitAsync(string id, GitCommitRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.CommitAsync(id, request, token), cancellationToken);

    public Task<GitOperationResult> MergeBranchAsync(string id, GitMergeRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.MergeBranchAsync(id, request, token), cancellationToken);

    public Task<GitOperationResult> FetchAsync(string id, GitFetchRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.FetchAsync(id, request, token), cancellationToken);

    public Task<GitOperationResult> PullAsync(string id, GitPullRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.PullAsync(id, request, token), cancellationToken);

    public Task<GitOperationResult> PushAsync(string id, GitPushRequest? request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.PushAsync(id, request, token), cancellationToken);

    public Task<GitPushPreviewDto> GetPushPreviewAsync(string id, string? localBranch, string? remote, string? remoteBranch, CancellationToken cancellationToken) =>
        ReadAsync(token => Client.GetPushPreviewAsync(id, localBranch, remote, remoteBranch, token), cancellationToken);

    public Task<IReadOnlyList<GitCommitDto>> GetLogAsync(string id, int limit, int skip, GitLogQuery? query, CancellationToken cancellationToken) =>
        ReadAsync(token => Client.GetLogAsync(id, limit, skip, query, token), cancellationToken);

    public Task<GitCommitDetailDto> GetCommitDetailAsync(string id, string sha, CancellationToken cancellationToken) =>
        ReadAsync(token => Client.GetCommitDetailAsync(id, sha, token), cancellationToken);

    public Task<GitDiffDto> GetDiffAsync(string id, string path, bool staged, string? @ref, CancellationToken cancellationToken) =>
        ReadAsync(token => Client.GetDiffAsync(id, path, staged, @ref, token), cancellationToken);

    public Task<GitOperationResult> RevertAsync(string id, GitRevertRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.RevertAsync(id, request, token), cancellationToken);

    public Task<GitConflictStateDto> GetConflictStateAsync(string id, CancellationToken cancellationToken) =>
        ReadAsync(token => Client.GetConflictStateAsync(id, token), cancellationToken);

    public Task<GitConflictFileDto> GetConflictAsync(string id, string path, CancellationToken cancellationToken) =>
        ReadAsync(token => Client.GetConflictAsync(id, path, token), cancellationToken);

    public Task<GitOperationResult> ConflictOperationAsync(string id, GitConflictOperationRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.ConflictOperationAsync(id, request, token), cancellationToken);

    public Task<GitOperationResult> ResolveConflictsAsync(string id, GitResolveRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.ResolveConflictsAsync(id, request, token), cancellationToken);

    public Task<GitOperationResult> ResetAsync(string id, GitResetRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.ResetAsync(id, request, token), cancellationToken);

    public Task<GitOperationResult> RestoreAsync(string id, GitRestoreRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.RestoreAsync(id, request, token), cancellationToken);

    public Task<GitOperationResult> StageAsync(string id, GitStageRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.StageAsync(id, request, token), cancellationToken);

    public Task<GitOperationResult> UnstageAsync(string id, GitUnstageRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.UnstageAsync(id, request, token), cancellationToken);

    public Task<GitRepositoryProbeDto> ProbeRepositoryAsync(string path, CancellationToken cancellationToken) =>
        ReadAsync(token => Client.ProbeRepositoryAsync(path, token), cancellationToken);

    public Task<GitOperationResult> InitRepositoryAsync(string path, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.InitRepositoryAsync(path, token), cancellationToken);

    public Task<IReadOnlyList<GitRemoteDto>> ListRemotesAsync(string id, CancellationToken cancellationToken) =>
        ReadAsync(token => Client.ListRemotesAsync(id, token), cancellationToken);

    public Task<GitOperationResult> AddRemoteAsync(string id, GitRemoteRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.AddRemoteAsync(id, request, token), cancellationToken);

    public Task<GitOperationResult> UpdateRemoteAsync(string id, string name, GitRemoteRequest request, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.UpdateRemoteAsync(id, name, request, token), cancellationToken);

    public Task<GitOperationResult> RemoveRemoteAsync(string id, string name, CancellationToken cancellationToken) =>
        ManageAsync(token => Client.RemoveRemoteAsync(id, name, token), cancellationToken);

    private Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        RunAsync(operation, manage: false, cancellationToken);

    private Task<T> ManageAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        RunAsync(operation, manage: true, cancellationToken);

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, bool manage, CancellationToken cancellationToken)
    {
        using var linked = lifetime.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken)
            : null;
        var token = linked?.Token ?? cancellationToken;
        CheckAccess(manage, token);
        var result = await operation(token);
        CheckAccess(manage, token);
        return result;
    }

    private void CheckAccess(bool manage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!permissions.IsGranted(appId, CoreAppPermissions.ServerGitRead)
            || manage && !permissions.IsGranted(appId, CoreAppPermissions.ServerGitManage))
            throw new UnauthorizedAccessException();
        if (!connection.IsCurrent)
            throw new InvalidOperationException("The application's Git connection has changed.");
    }
}
