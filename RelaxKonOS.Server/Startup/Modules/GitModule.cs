using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Git;
using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.Startup.Modules;

/// <summary>
/// Git repository editing and source builds. The module owns the git CLI service, the repository
/// service, the editor bridge, the build ledger and every Git route.
///
/// <para>
/// The module graph is releasable: the Host registers only lease-taking gates for
/// <see cref="IGitRepositoryService"/> and <see cref="IGitBuildService"/>, and the real objects are
/// created by <see cref="GitModuleRuntime"/> on the first business call. The stateless
/// <see cref="GitTextEditor"/> bridge stays in the root graph because it owns no resources; every call
/// it makes still goes through the gate.
/// </para>
///
/// <para>
/// The only thing that crosses the domain boundary is <see cref="IDeploymentImageBuildSource"/>: the
/// deployment domain asks "is this build deployable" without referencing Git types, and this module
/// replaces the Host's unavailable default with a gate that can wake Git when it is dormant.
/// </para>
/// </summary>
internal sealed class GitModule : IServerModule, IServerModuleResidency, IServerModuleDeactivationGuard
{
    public string Id => ServerModules.GitId;
    public IReadOnlyList<string> Capabilities => [ServerCapabilities.Git];
    public IReadOnlyList<string> Requires => [];

    public IServerModuleRuntimeFactory RuntimeFactory { get; } = new GitModuleRuntimeFactory();
    public ValueTask<IReadOnlyList<ServerModuleOrphanedWork>> InspectOrphanedWorkAsync(
        IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var active = services.GetService<ModuleResidencyCoordinator>()?.HasBackgroundWork(Id) == true;
        var work = GitBuildService.InspectLedger(context.ContentRootPath,
            context.Configuration.GetSection("GitBuilds").Get<GitBuildOptions>() ?? new(), active);
        if (active)
            work = [.. work, new(Id, "Git background execution or cleanup is still active.")];
        return ValueTask.FromResult(work);
    }

    public void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        // Gates, not implementations: a lease loads the runtime, the call runs, the lease releases and
        // the idle sweep may then release the module.
        builder.Services.AddSingleton<IGitRepositoryService>(services =>
            ModuleServiceProxy<IGitRepositoryService>.Create(
                services.GetRequiredService<ModuleResidencyCoordinator>(), Id));
        builder.Services.AddSingleton<IGitBuildService>(services =>
            ModuleServiceProxy<IGitBuildService>.Create(
                services.GetRequiredService<ModuleResidencyCoordinator>(), Id));
        builder.Services.AddSingleton<IDeploymentImageBuildSource, ResidentDeploymentImageBuildSource>();
        // Stateless bridge over the two gates; it holds no module-owned resource itself.
        builder.Services.AddScoped<GitTextEditor>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        endpoints.MapGitEndpoints();
        endpoints.MapGitBuildEndpoints();
    }
}
