using System.Buffers;

namespace RelaxKonOS.Server.Proxy;

public interface IProxyConnectionSnapshotReader
{
    int Count { get; }
    long Parse(ReadOnlySequence<byte> buffer, bool final, CancellationToken cancellationToken);
}

public readonly record struct ProxyConnectionSnapshotEntry(
    string Id, string Network, string Source, string Destination, string Rule, string Chains, DateTimeOffset Start);
