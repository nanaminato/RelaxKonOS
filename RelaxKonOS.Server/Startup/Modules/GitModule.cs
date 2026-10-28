using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Git;

namespace RelaxKonOS.Server.Startup.Modules;

/// <summary>
/// Git repository editing and source builds. The module owns the git CLI service, the repository
/// service, the editor bridge, the build ledger and every Git route.
///
/// The only thing that crosses the boundary is <see cref="IDeploymentImageBuildSource"/>: the
/// deployment domain asks "is this build deployable" without referencing Git types, and this module
/// registers the real implementation after the Host has registered the unavailable default.
/// </summary>
internal sealed class GitModule : IServerModule
{
    public string Id => ServerModules.GitId;
    public IReadOnlyList<string> Capabilities => [ServerCapabilities.Git];
    public IReadOnlyList<string> Requires => [];

    public void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        // Invokes the host git CLI as the host user; credentials are handled entirely by the host
        // git credential helper. Singleton because it holds the per-repository write semaphore.
        builder.Services.AddSingleton<IHostGitCli, HostGitCli>();
        builder.Services.AddSingleton<IGitRepositoryService, LocalGitRepositoryService>();
        builder.Services.AddScoped<GitTextEditor>();
        builder.Services.AddSingleton(context.Configuration.GetSection("GitBuilds").Get<GitBuildOptions>() ?? new GitBuildOptions());
        builder.Services.AddSingleton<GitBuildService>();
        builder.Services.AddSingleton<IDeploymentImageBuildSource, GitDeploymentImageBuildSource>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        endpoints.MapGitEndpoints();
        endpoints.MapGitBuildEndpoints();
    }
}
