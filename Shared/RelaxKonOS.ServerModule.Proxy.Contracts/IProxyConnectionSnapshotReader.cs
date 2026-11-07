using System.Buffers;
using System.Text.Json;

namespace RelaxKonOS.Server.Proxy;

public interface IProxyConnectionSnapshotReader
{
    long Parse(ReadOnlySequence<byte> buffer, bool final, CancellationToken cancellationToken);
}
