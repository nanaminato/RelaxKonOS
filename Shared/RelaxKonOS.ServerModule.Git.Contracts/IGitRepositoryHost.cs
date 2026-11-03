using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Server.Git;

/// <summary>Materialized registration metadata; never an EF entity or query provider.</summary>
public sealed class GitRepositoryRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public GitRepositoryDto ToDto(string? currentBranch = null, string? defaultBranch = null,
        int headAhead = 0, int headBehind = 0, bool hasUpstream = false, int uncommittedCount = 0) =>
        new(Id.ToString(), Name, Path, currentBranch, defaultBranch, headAhead, headBehind, hasUpstream, uncommittedCount);
}

public sealed record GitCredentialSetting(string Key, string ValueJson);

/// <summary>Short Host-owned credential edit; acquired only during a leased module call.</summary>
public interface IGitCredentialUpdate : IAsyncDisposable
{
    IReadOnlyList<GitCredentialSetting> Settings { get; }
    Task SaveAsync(string key, string? valueJson, CancellationToken cancellationToken);
}

public interface IGitRepositoryStorage
{
    Task<IReadOnlyList<GitRepositoryRecord>> ListAsync(Guid userId, CancellationToken cancellationToken);
    Task<GitRepositoryRecord?> FindAsync(Guid id, Guid userId, CancellationToken cancellationToken);
    Task AddAsync(GitRepositoryRecord repository, CancellationToken cancellationToken);
    Task<bool> RemoveAsync(Guid id, Guid userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<GitCredentialSetting>> CredentialsAsync(Guid userId, CancellationToken cancellationToken);
    Task<GitCredentialSetting?> FindCredentialAsync(Guid userId, string key, CancellationToken cancellationToken);
    Task<IGitCredentialUpdate> BeginCredentialUpdateAsync(Guid userId, string? key, CancellationToken cancellationToken);
}

public sealed record GitHostCommandResult(bool Success, string Output, string Error, int ExitCode = -1);

/// <summary>Current caller and closed Git/file operations. No root DI, principal cache or arbitrary Helper action.</summary>
public interface IGitRepositoryHost
{
    bool UsesHelper { get; }
    Guid? CurrentUserId { get; }
    string UserHomeDirectory { get; }
    bool IsPathWithin(string root, string candidate);
    Task<GitHostCommandResult> ExecuteHelperAsync(string root, IReadOnlyList<string> arguments,
        GitCredentialRequest? credentials, CancellationToken cancellationToken);
    Task<FileSystemEntryDto?> GetInfoAsync(string path, CancellationToken cancellationToken);
    Task<GitConflictSnapshot> ReadConflictAsync(string root, string relative, CancellationToken cancellationToken);
    Task<bool> WriteConflictAsync(string root, string relative, string contentBase64, string expectedHash, CancellationToken cancellationToken);
}
