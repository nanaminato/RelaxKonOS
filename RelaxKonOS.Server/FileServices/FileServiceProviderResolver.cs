using RelaxKonOS.Protocol.FileServices;

namespace RelaxKonOS.Server.FileServices;

public sealed class FileServiceProviderResolver(IEnumerable<IFileServiceProvider> providers) : IFileServiceProviderResolver
{
    private readonly IReadOnlyList<IFileServiceProvider> _providers = providers.ToArray();
    public IFileServiceProvider? Resolve(FileServiceProtocol protocol) => _providers.FirstOrDefault(x => x.Protocol == protocol && x.IsApplicable);
}
