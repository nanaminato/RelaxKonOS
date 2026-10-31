namespace RelaxKonOS.ModuleHost.Sdk;

/// <summary>
/// Narrow v2 entry ABI. The ModuleHost owns transport, authentication and privilege brokering; an
/// entry only owns its own lifecycle and optional health probe.
/// </summary>
public interface IModuleHostEntry
{
    string Id { get; }

    ValueTask StartAsync(IModuleHostContext context, CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>The only Host facts an isolated module may read at startup.</summary>
public interface IModuleHostContext
{
    string ModuleId { get; }
    string ModuleDataPath { get; }
    string HostApiVersion { get; }
}

public sealed record ModuleHostHealth(bool Healthy, string? ProblemCode = null);

public interface IModuleHostHealth
{
    ValueTask<ModuleHostHealth> CheckHealthAsync(CancellationToken cancellationToken);
}
