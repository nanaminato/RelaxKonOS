using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RelaxKonOS.ServerModule.Abstractions;

/// <summary>
/// Host ABI for an independently installed, official, in-process server module. The Host provides
/// this assembly and never lets the package bring its own copy, so the interface type identity is
/// stable across the Host and the module.
/// </summary>
public interface IInstalledServerModule
{
    /// <summary>Stable module identity. The <c>relaxkonos.</c> prefix is reserved for official modules.</summary>
    string Id { get; }

    /// <summary>Host capabilities this module provides while enabled.</summary>
    IReadOnlyList<string> Capabilities { get; }

    /// <summary>Other module IDs that must be enabled before this one.</summary>
    IReadOnlyList<string> Requires { get; }

    void ConfigureServices(IServiceCollection services, InstalledModuleContext context);

    void MapEndpoints(IEndpointRouteBuilder endpoints, InstalledModuleContext context);
}

/// <summary>
/// The narrow Host surface an installed module may read. It deliberately has no built
/// <c>WebApplication</c>, no service provider, no privileged transport and no arbitrary file paths.
/// </summary>
public sealed record InstalledModuleContext(
    IConfiguration Configuration,
    IHostEnvironment Environment,
    string ContentRootPath,
    string ModuleDataPath,
    string Platform,
    string StorageProvider);
