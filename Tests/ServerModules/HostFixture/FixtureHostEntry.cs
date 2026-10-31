using RelaxKonOS.ModuleHost.Sdk;

namespace HostFixture;

/// <summary>Test-only v2 entry used by the isolated ModuleHost skeleton.</summary>
public sealed class FixtureHostEntry : IModuleHostEntry, IModuleHostHealth
{
    public string Id => "fixture-host";
    public ValueTask StartAsync(IModuleHostContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask<ModuleHostHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ModuleHostHealth(true));
}
