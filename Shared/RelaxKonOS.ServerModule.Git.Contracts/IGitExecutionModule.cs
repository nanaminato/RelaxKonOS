using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.Git;

/// <summary>Host-owned dependencies borrowed for one execution generation. No root service provider.</summary>
public sealed record GitExecutionContext(string ContentRootPath, IDataProtectionProvider Protection,
    GitBuildOptions Options, IModuleWorkLeaseSource WorkLeases, ILoggerFactory Logs,
    Lazy<IGitRepositoryStorage> RepositoryStorage, Lazy<IGitRepositoryHost> RepositoryHost);

/// <summary>Current bundled Git execution ABI. Implementations never register Host routes or services.</summary>
public interface IGitExecutionModule : IAsyncDisposable
{
    IHostGitCli Cli { get; }
    IGitRepositoryService Repositories { get; }
    IGitBuildService Builds { get; }
    string? UnloadBlocker { get; }
}
