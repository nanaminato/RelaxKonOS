using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using RelaxKonOS.ModuleHost.Sdk;

namespace HostFixture;

/// <summary>Test-only v2 entry used by the isolated ModuleHost skeleton.</summary>
public sealed class FixtureHostEntry : IModuleHostEntry, IModuleHostHealth, IModuleHostHttp
{
    public string Id => "fixture-host";
    public async ValueTask StartAsync(IModuleHostContext context, CancellationToken cancellationToken)
    {
        await context.Storage.WriteAsync("hello.txt", Encoding.UTF8.GetBytes("hello from module storage"),
            cancellationToken);
        context.Logger.LogInformation("Fixture module storage initialized.");
    }
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask<ModuleHostHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ModuleHostHealth(true));
    public void MapEndpoints(IEndpointRouteBuilder endpoints, IModuleHostContext context)
    {
        endpoints.MapGet("/hello", () => "hello from module host");
        endpoints.MapGet("/storage", async () =>
        {
            var bytes = await context.Storage.ReadAsync("hello.txt", 4096, CancellationToken.None);
            return bytes is null ? Results.NotFound() : Results.Text(Encoding.UTF8.GetString(bytes));
        });
    }
}
