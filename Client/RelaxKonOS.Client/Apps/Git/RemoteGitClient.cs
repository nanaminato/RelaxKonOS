using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git;

/// <summary>Typed JWT client for the server's Git repository facade. Uses absolute URIs per request (no BaseAddress mutation).
/// Routes use <see cref="GitApiRoutes"/> constants; {id}/{name} are escaped via <see cref="Uri.EscapeDataString"/>.</summary>
public sealed class RemoteGitClient(HttpClient http, IAuthSession session) : IRemoteGitClient
{
    // ── Host Git engine probe & install ──
    public Task<GitEngineStatusDto> GetEngineStatusAsync(CancellationToken cancellationToken = default)
        => SendAsync<GitEngineStatusDto>(HttpMethod.Get, GitApiRoutes.EngineStatus, null, cancellationToken);

    public Task<GitEngineInstallResult> InstallEngineAsync(CancellationToken cancellationToken = default)
        => SendAsync<GitEngineInstallResult>(HttpMethod.Post, GitApiRoutes.EngineInstall, null, cancellationToken);

    public Task<IReadOnlyList<GitRepositoryDto>> ListRepositoriesAsync(CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<GitRepositoryDto>>(HttpMethod.Get, GitApiRoutes.Repositories, null, cancellationToken);

    public async Task<GitRepositoryDto?> GetRepositoryAsync(string id, CancellationToken cancellationToken = default)
        => await TrySendAsync<GitRepositoryDto>(GitApiRoutes.RepositoryById.Replace("{id}", Uri.EscapeDataString(id)), cancellationToken);

    public Task<GitRepositoryDto> RegisterRepositoryAsync(GitRepositoryRegistration registration, CancellationToken cancellationToken = default)
        => SendAsync<GitRepositoryDto>(HttpMethod.Post, GitApiRoutes.Repositories, registration, cancellationToken);

    public async Task<bool> UnregisterRepositoryAsync(string id, CancellationToken cancellationToken = default)
    {
        var route = GitApiRoutes.RepositoryById.Replace("{id}", Uri.EscapeDataString(id));
        var response = await SendRawAsync(HttpMethod.Delete, route, null, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public Task<GitStatusDto> GetStatusAsync(string id, CancellationToken cancellationToken = default)
        => SendAsync<GitStatusDto>(HttpMethod.Get, GitApiRoutes.Status.Replace("{id}", Uri.EscapeDataString(id)), null, cancellationToken);

    public Task<IReadOnlyList<GitBranchDto>> ListBranchesAsync(string id, CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<GitBranchDto>>(HttpMethod.Get, GitApiRoutes.Branches.Replace("{id}", Uri.EscapeDataString(id)), null, cancellationToken);

    public Task<GitBranchComparisonDto> CompareBranchAsync(string id, string name, CancellationToken cancellationToken = default)
        => SendAsync<GitBranchComparisonDto>(HttpMethod.Get,
            GitApiRoutes.BranchComparison.Replace("{id}", Uri.EscapeDataString(id)).Replace("{name}", Uri.EscapeDataString(name)),
            null, cancellationToken);

    public Task<GitOperationResult> CreateBranchAsync(string id, GitBranchCreateRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Post, GitApiRoutes.Branches.Replace("{id}", Uri.EscapeDataString(id)), request, cancellationToken);

    public Task<GitOperationResult> DeleteBranchAsync(string id, string name, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Delete, GitApiRoutes.BranchByName.Replace("{id}", Uri.EscapeDataString(id)).Replace("{name}", Uri.EscapeDataString(name)), null, cancellationToken);

    public Task<GitOperationResult> RenameBranchAsync(string id, string name, GitBranchRenameRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Post,
            GitApiRoutes.RenameBranch.Replace("{id}", Uri.EscapeDataString(id)).Replace("{name}", Uri.EscapeDataString(name)),
            request, cancellationToken);

    public Task<GitOperationResult> SetBranchTrackingAsync(string id, string name, GitBranchTrackingRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Put,
            GitApiRoutes.BranchTracking.Replace("{id}", Uri.EscapeDataString(id)).Replace("{name}", Uri.EscapeDataString(name)),
            request, cancellationToken);

    public Task<GitOperationResult> CheckoutAsync(string id, GitCheckoutRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Post, GitApiRoutes.Checkout.Replace("{id}", Uri.EscapeDataString(id)), request, cancellationToken);

    public Task<GitOperationResult> CommitAsync(string id, GitCommitRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Post, GitApiRoutes.Commit.Replace("{id}", Uri.EscapeDataString(id)), request, cancellationToken);

    public Task<GitOperationResult> MergeBranchAsync(string id, GitMergeRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Post, GitApiRoutes.Merge.Replace("{id}", Uri.EscapeDataString(id)), request, cancellationToken);

    public Task<GitOperationResult> FetchAsync(string id, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Post, GitApiRoutes.Fetch.Replace("{id}", Uri.EscapeDataString(id)), null, cancellationToken);

    public Task<GitOperationResult> PullAsync(string id, GitPullRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Post, GitApiRoutes.Pull.Replace("{id}", Uri.EscapeDataString(id)), request, cancellationToken);

    public Task<GitOperationResult> PushAsync(string id, GitPushRequest? request = null, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Post, GitApiRoutes.Push.Replace("{id}", Uri.EscapeDataString(id)), request, cancellationToken);

    public async Task<IReadOnlyList<GitCommitDto>> GetLogAsync(string id, int limit = 200, int skip = 0,
        GitLogQuery? query = null, CancellationToken cancellationToken = default)
    {
        var route = $"{GitApiRoutes.Log.Replace("{id}", Uri.EscapeDataString(id))}?limit={limit}&skip={skip}";
        if (query is not null)
        {
            static string Add(string route, string key, string? value)
                => string.IsNullOrWhiteSpace(value) ? route : $"{route}&{key}={Uri.EscapeDataString(value)}";
            route = Add(route, "reference", query.Reference);
            route = Add(route, "search", query.Search);
            route = Add(route, "author", query.Author);
            route = Add(route, "path", query.Path);
            route = Add(route, "dateRange", query.DateRange);
            if (query.CaseSensitive) route += "&caseSensitive=true";
            if (query.UseRegex) route += "&useRegex=true";
        }
        return await SendAsync<IReadOnlyList<GitCommitDto>>(HttpMethod.Get, route, null, cancellationToken);
    }

    public Task<GitCommitDetailDto> GetCommitDetailAsync(string id, string sha, CancellationToken cancellationToken = default)
    {
        var route = GitApiRoutes.CommitDetail
            .Replace("{id}", Uri.EscapeDataString(id))
            .Replace("{sha}", Uri.EscapeDataString(sha));
        return SendAsync<GitCommitDetailDto>(HttpMethod.Get, route, null, cancellationToken);
    }

    public async Task<GitDiffDto> GetDiffAsync(string id, string path, bool staged = false, string? @ref = null, CancellationToken cancellationToken = default)
    {
        var route = $"{GitApiRoutes.Diff.Replace("{id}", Uri.EscapeDataString(id))}?path={Uri.EscapeDataString(path)}";
        if (staged) route += "&staged=true";
        if (!string.IsNullOrEmpty(@ref)) route += $"&ref={Uri.EscapeDataString(@ref)}";
        return await SendAsync<GitDiffDto>(HttpMethod.Get, route, null, cancellationToken);
    }

    public Task<GitOperationResult> RevertAsync(string id, GitRevertRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Post, GitApiRoutes.Revert.Replace("{id}", Uri.EscapeDataString(id)), request, cancellationToken);

    public Task<GitOperationResult> ResolveConflictsAsync(string id, GitResolveRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Post, GitApiRoutes.Resolve.Replace("{id}", Uri.EscapeDataString(id)), request, cancellationToken);

    public Task<GitOperationResult> ResetAsync(string id, GitResetRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Post, GitApiRoutes.Reset.Replace("{id}", Uri.EscapeDataString(id)), request, cancellationToken);

    public Task<GitOperationResult> RestoreAsync(string id, GitRestoreRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Post, GitApiRoutes.Restore.Replace("{id}", Uri.EscapeDataString(id)), request, cancellationToken);

    public Task<GitOperationResult> StageAsync(string id, GitStageRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Post, GitApiRoutes.Stage.Replace("{id}", Uri.EscapeDataString(id)), request, cancellationToken);

    public Task<GitOperationResult> UnstageAsync(string id, GitUnstageRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Post, GitApiRoutes.Unstage.Replace("{id}", Uri.EscapeDataString(id)), request, cancellationToken);

    public Task<GitRepositoryProbeDto> ProbeRepositoryAsync(string path, CancellationToken cancellationToken = default)
        => SendAsync<GitRepositoryProbeDto>(HttpMethod.Get, $"{GitApiRoutes.Probe}?path={Uri.EscapeDataString(path)}", null, cancellationToken);

    public Task<GitOperationResult> InitRepositoryAsync(string path, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Post, $"{GitApiRoutes.Init}?path={Uri.EscapeDataString(path)}", null, cancellationToken);

    public Task<IReadOnlyList<GitRemoteDto>> ListRemotesAsync(string id, CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<GitRemoteDto>>(HttpMethod.Get, GitApiRoutes.Remotes.Replace("{id}", Uri.EscapeDataString(id)), null, cancellationToken);

    public Task<GitOperationResult> AddRemoteAsync(string id, GitRemoteRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Post, GitApiRoutes.Remotes.Replace("{id}", Uri.EscapeDataString(id)), request, cancellationToken);

    public Task<GitOperationResult> UpdateRemoteAsync(string id, string name, GitRemoteRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Put,
            GitApiRoutes.RemoteByName.Replace("{id}", Uri.EscapeDataString(id)).Replace("{name}", Uri.EscapeDataString(name)),
            request, cancellationToken);

    public Task<GitOperationResult> RemoveRemoteAsync(string id, string name, CancellationToken cancellationToken = default)
        => SendAsync<GitOperationResult>(HttpMethod.Delete,
            GitApiRoutes.RemoteByName.Replace("{id}", Uri.EscapeDataString(id)).Replace("{name}", Uri.EscapeDataString(name)),
            null, cancellationToken);

    // ── HTTP helpers (same pattern as RemoteDockerClient / RemoteFirewallClient) ──

    private async Task<T?> TrySendAsync<T>(string route, CancellationToken cancellationToken) where T : class
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null)
            throw new InvalidOperationException("RelaxKonOS session is not authenticated.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(session.ServerUrl), route.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        var response = await SendRawAsync(method, route, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException("RelaxKonOS returned an empty response.");
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null)
            throw new InvalidOperationException("RelaxKonOS session is not authenticated.");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), route.TrimStart('/')))
        {
            Content = body is null ? null : JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        return await http.SendAsync(request, cancellationToken);
    }
}
