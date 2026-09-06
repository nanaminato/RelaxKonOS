using RemoteOS.Core.Applications;

namespace RemoteOS.AppSDK;

/// <summary>Host-compiled mapping used to prove the identity of a built-in application.</summary>
public interface IBuiltInApplicationFactoryRegistry
{
    IReadOnlyCollection<BuiltInApplicationDefinition> Definitions { get; }
    bool TryGet(string builtinKey, out BuiltInApplicationDefinition definition);
}

/// <summary>
/// Both the factory and manifest are supplied by Host code. Disk metadata is only compared to
/// this definition and can never create or alter one.
/// </summary>
public sealed record BuiltInApplicationDefinition(
    string BuiltInKey,
    AppId AppId,
    ApplicationManifest Manifest,
    Func<IServiceProvider, IRemoteApplication> Factory);

public sealed class BuiltInApplicationFactoryRegistry : IBuiltInApplicationFactoryRegistry
{
    private readonly Dictionary<string, BuiltInApplicationDefinition> _definitions;

    public BuiltInApplicationFactoryRegistry(IEnumerable<BuiltInApplicationDefinition> definitions)
    {
        _definitions = new Dictionary<string, BuiltInApplicationDefinition>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.BuiltInKey)
                || definition.AppId != definition.Manifest.Id
                || !_definitions.TryAdd(definition.BuiltInKey, definition))
                throw new InvalidOperationException("Built-in application definitions must have unique Host-owned keys and matching ids.");
        }
    }

    public IReadOnlyCollection<BuiltInApplicationDefinition> Definitions => _definitions.Values.ToArray();

    public bool TryGet(string builtinKey, out BuiltInApplicationDefinition definition) =>
        _definitions.TryGetValue(builtinKey, out definition!);
}
