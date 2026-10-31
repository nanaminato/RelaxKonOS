using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.ServerModule.Abstractions;

namespace FixtureModule;

/// <summary>Test-only installed module proving the Host can load an external assembly through the ABI.</summary>
public sealed class FixtureInstalledModule : IInstalledServerModule, IInstalledServerModuleHealth
{
    public string Id => "fixture-module";
    public IReadOnlyList<string> Capabilities => [];
    public IReadOnlyList<string> Requires => [];

    public void ConfigureServices(IServiceCollection services, InstalledModuleContext context) =>
        services.AddSingleton(new FixtureMarker(context.ContentRootPath));

    public void MapEndpoints(IEndpointRouteBuilder endpoints, InstalledModuleContext context) =>
        endpoints.MapGet("/api/v1.0/fixture-module/ping", () => "pong");

    public ValueTask<InstalledModuleHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new InstalledModuleHealth(true));
}

public sealed record FixtureMarker(string ContentRootPath);
