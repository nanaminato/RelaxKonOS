using Microsoft.Extensions.Logging;

namespace RelaxKonOS.Server.Git;

public sealed class GitExecutionModule(GitExecutionContext context) : IGitExecutionModule
{
    private readonly Lazy<GitBuildService> _builds = new(() => new GitBuildService(context.ContentRootPath, context.Protection,
        context.Options, context.WorkLeases, new GitBuildCommandRunner(), context.Logs.CreateLogger<GitBuildService>()));
    private readonly Lazy<LocalGitRepositoryService> _repositories = new(() => new LocalGitRepositoryService(
        context.RepositoryStorage.Value, new HostGitCli(), context.Protection,
        context.Logs.CreateLogger<LocalGitRepositoryService>(), context.RepositoryHost.Value));
    public IGitRepositoryService Repositories => _repositories.Value;
    public IHostGitCli Cli { get; } = new HostGitCli();
    public IGitBuildService Builds => _builds.Value;
    public string? UnloadBlocker => _builds.IsValueCreated && _builds.Value.RequiresRecovery ? "module-recovery-blocked"
        : _builds.IsValueCreated && _builds.Value.HasActiveWork ? "module-active-work" : null;
    public ValueTask DisposeAsync()
    {
        if (_repositories.IsValueCreated) _repositories.Value.Dispose();
        if (_builds.IsValueCreated) _builds.Value.Dispose();
        return ValueTask.CompletedTask;
    }
}
