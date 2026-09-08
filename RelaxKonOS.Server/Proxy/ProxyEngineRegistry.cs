namespace RelaxKonOS.Server.Proxy;

public sealed class ProxyEngineRegistry(IEnumerable<IProxyEngine> engines) : IProxyEngineRegistry
{
    private readonly IReadOnlyList<IProxyEngine> _engines = engines.ToArray();
    public IProxyEngine? Find(string engineId) => _engines.FirstOrDefault(engine => string.Equals(engine.EngineId, engineId, StringComparison.Ordinal));
    public IReadOnlyList<IProxyEngine> List() => _engines;
}
