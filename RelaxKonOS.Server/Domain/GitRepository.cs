using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Server.Domain;

/// <summary>Registered Git repository domain model. Only registration metadata is persisted;
/// branch/commit/status/diff are real-time <c>git</c> results, never stored. Corresponds to SQLite git_repositories table.</summary>
public sealed class GitRepository
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public GitRepositoryDto ToDto(
        string? currentBranch = null,
        string? defaultBranch = null,
        int headAhead = 0,
        int headBehind = 0,
        bool hasUpstream = false,
        int uncommittedCount = 0) => new(Id.ToString(), Name, Path, currentBranch, defaultBranch, headAhead, headBehind, hasUpstream, uncommittedCount);
}
