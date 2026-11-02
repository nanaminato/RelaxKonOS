using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Server.Components;

/// <summary>A component refused the request for a domain reason; the code is a stable client contract.</summary>
public sealed class ComponentLifecycleException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}

/// <summary>
/// One host's lifecycle adapter for one component. Everything the caller sees is already normalized
/// (<see cref="ComponentState"/>, <see cref="ComponentPlanResult"/>), so a caller never needs to know
/// whether the product is a ledgered archive runtime or a distribution package.
/// </summary>
public interface IComponentLifecycleAdapter
{
    string ComponentId { get; }
    /// <summary>Declared modes for this host, including modes that are declared but not implemented.</summary>
    IReadOnlyList<ComponentModeSupport> Modes { get; }
    Task<ComponentStateView> StateAsync(CancellationToken ct);
    Task<ComponentPlanResult> PlanAsync(ComponentPlanRequest request, CancellationToken ct);
    /// <summary>
    /// Carries out exactly the frozen change, or refuses. It must re-measure before acting: the host
    /// may have moved between the plan and this call.
    /// </summary>
    Task<ComponentExecutionResult> ExecuteAsync(ComponentExecuteRequest request, CancellationToken ct);
}

/// <summary>
/// The closed set of components this build can answer for. An unregistered component is unknown,
/// not unsupported: the distinction is what stops a typo from looking like a platform gap.
/// </summary>
public sealed class ComponentLifecycleRegistry
{
    private readonly Dictionary<string, IComponentLifecycleAdapter> _adapters;

    public ComponentLifecycleRegistry(IEnumerable<IComponentLifecycleAdapter> adapters)
    {
        _adapters = new(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            if (!ComponentIds.Known(adapter.ComponentId))
                throw new InvalidOperationException($"Component adapter '{adapter.ComponentId}' is not a known component id.");
            if (!_adapters.TryAdd(adapter.ComponentId, adapter))
                throw new InvalidOperationException($"Component '{adapter.ComponentId}' has more than one adapter.");
        }
    }

    public IReadOnlyList<string> Registered => [.. _adapters.Keys.OrderBy(id => id, StringComparer.Ordinal)];

    public IComponentLifecycleAdapter Require(string? componentId)
    {
        if (string.IsNullOrWhiteSpace(componentId) || !_adapters.TryGetValue(componentId, out var adapter))
            throw new ComponentLifecycleException(ComponentProblemCodes.UnknownComponent);
        return adapter;
    }

    public async Task<ComponentPlanResult> PlanAsync(ComponentPlanRequest request, CancellationToken ct)
    {
        var domain = Require(request.ComponentId);
        var (provider, problem) = ComponentInstallerProviderRegistry.Host.Resolve(domain, request);
        return provider is null ? new(request, null, null, [problem!], [])
            : await provider.PlanAsync(domain, request, ct);
    }

    public async Task<ComponentExecutionResult> ExecuteAsync(ComponentExecuteRequest frozen, CancellationToken ct)
    {
        if (frozen.ComponentId != frozen.Request.ComponentId)
            return new(frozen.ComponentId, ComponentExecutionOutcome.Refused, [ComponentProblemCodes.RequestInvalid], [], null, null);
        var domain = Require(frozen.ComponentId);
        var (provider, problem) = ComponentInstallerProviderRegistry.Host.Resolve(domain, frozen.Request);
        return provider is null ? new(frozen.ComponentId, ComponentExecutionOutcome.Refused, [problem!], [], null, null)
            : await provider.ExecuteAsync(domain, frozen, ct);
    }
}

public static class ComponentRevision
{
    /// <summary>
    /// A stable revision derived from observed facts, for a component that has no ledger. It is not a
    /// counter: it changes exactly when the facts it is built from change, which is what a caller
    /// checking "did anything move since I prepared this" needs.
    /// </summary>
    public static long Of(params string?[] parts)
    {
        var material = string.Join('\u001f', parts.Select(part => part ?? "-"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        var value = BitConverter.ToInt64(hash, 0) & long.MaxValue;
        return value == 0 ? 1 : value;
    }
}

public static class ComponentPlatforms
{
    public static string Name => OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsMacOS() ? "macos" : "unknown";
}
