using System.Security.Claims;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Files;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.UserExecution;

namespace RelaxKonOS.Server.Git;

/// <summary>Resolves the current authenticated identity per call; never caches a principal in a module.</summary>
public sealed class HostGitRepositoryAccess(IServiceScopeFactory scopes, IUserExecutionTransport transport,
    IServerModeResolver mode, UserExecutionBackendSelection backend, IHttpContextAccessor http) : IGitRepositoryHost
{
    public bool UsesHelper => mode.Mode == ServerMode.System && backend.Backend == UserExecutionBackend.Helper;
    public Guid? CurrentUserId => Guid.TryParse(http.HttpContext?.User.FindFirstValue("sub")
        ?? http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    private UserExecutionContext CurrentContext()
    {
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IUserExecutionContextResolver>().Resolve(
            http.HttpContext?.User ?? throw new InvalidOperationException("Authenticated identity required."));
    }
    public string UserHomeDirectory => CurrentContext().Identity.HomeDirectory;
    public bool IsPathWithin(string root, string candidate) => PathSafety.IsWithin(root, candidate);

    public async Task<GitHostCommandResult> ExecuteHelperAsync(string root, IReadOnlyList<string> arguments,
        GitCredentialRequest? credentials, CancellationToken cancellationToken)
    {
        if (!UsesHelper) throw new InvalidOperationException("Git Helper execution is unavailable.");
        if (!UserExecutionGitPolicy.IsAllowed(arguments)) return new(false, "", "git_domain_arguments_rejected");
        if (http.HttpContext?.User is null) return new(false, "", "user_execution_context_unavailable");
        try
        {
            var response = await transport.ExecuteAsync(new(CurrentContext().Identity, UserExecutionOperationKind.GitExecute,
                Path: root, GitArguments: arguments, GitCredentials: credentials is null ? null : new(credentials.Username, credentials.Password),
                OperationId: Guid.NewGuid()), cancellationToken);
            if (!response.Success || string.IsNullOrWhiteSpace(response.OutputBase64))
                return new(false, "", response.ProblemCode == UserExecutionProblemCode.NotFound ? "path_not_found" : "user_execution_unavailable");
            var value = JsonSerializer.Deserialize<GitExecutionResult>(Convert.FromBase64String(response.OutputBase64), RelaxKonOSJsonOptions.Default);
            return value is null ? new(false, "", "user_execution_invalid_result") : new(value.Success, value.Output, value.Error, value.ExitCode);
        }
        catch (Exception error) when (error is UserExecutionException or InvalidOperationException or JsonException or FormatException)
        { return new(false, "", "user_execution_unavailable"); }
    }

    private async Task<T> FileCallAsync<T>(UserExecutionOperationKind operation, string root, string? relative,
        string? content, string? expectedHash, CancellationToken cancellationToken)
    {
        if (mode.Mode == ServerMode.System)
        {
            var response = await transport.ExecuteAsync(new(CurrentContext().Identity, operation, Path: root, FileName: relative,
                ContentBase64: content, ExpectedSha256: expectedHash, OperationId: Guid.NewGuid()), cancellationToken);
            if (!response.Success || response.OutputBase64 is null) throw new InvalidOperationException("Conflict file access is unavailable.");
            return JsonSerializer.Deserialize<T>(Convert.FromBase64String(response.OutputBase64), RelaxKonOSJsonOptions.Default)!;
        }
        cancellationToken.ThrowIfCancellationRequested();
        object? value = operation switch
        {
            UserExecutionOperationKind.FileGetInfo => new LocalFileService(mode).GetInfo(root),
            UserExecutionOperationKind.GitConflictSnapshot => GitConflictFileAccess.Read(root, relative!),
            UserExecutionOperationKind.GitConflictWrite => GitConflictFileAccess.Write(root, relative!, Convert.FromBase64String(content!), expectedHash!),
            _ => throw new ArgumentException("Invalid conflict I/O operation."),
        };
        return (T)value!;
    }
    public Task<FileSystemEntryDto?> GetInfoAsync(string path, CancellationToken cancellationToken) =>
        FileCallAsync<FileSystemEntryDto?>(UserExecutionOperationKind.FileGetInfo, path, null, null, null, cancellationToken);
    public Task<GitConflictSnapshot> ReadConflictAsync(string root, string relative, CancellationToken cancellationToken) =>
        FileCallAsync<GitConflictSnapshot>(UserExecutionOperationKind.GitConflictSnapshot, root, relative, null, null, cancellationToken);
    public Task<bool> WriteConflictAsync(string root, string relative, string contentBase64, string expectedHash, CancellationToken cancellationToken) =>
        FileCallAsync<bool>(UserExecutionOperationKind.GitConflictWrite, root, relative, contentBase64, expectedHash, cancellationToken);

    private sealed record GitExecutionResult(bool Success, int ExitCode, string Output, string Error);
}
