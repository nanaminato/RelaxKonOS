using RelaxKonOS.Client.Services;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Models;

/// <summary>Caches first pages within the current Explorer session.</summary>
internal sealed class CloudDirectoryCache(Func<DateTimeOffset>? clock = null)
{
    private readonly DirectoryListingCache<CloudDirectoryDto> _pages = new(clock);

    public bool TryGet(Guid connection, string? folder, out CloudDirectoryDto page) =>
        _pages.TryGet(Key(connection, folder), out page);

    public void Put(Guid connection, string? folder, CloudDirectoryDto page) =>
        _pages.Put(Key(connection, folder), page, page.Entries.Count);

    public void Remove(Guid connection, string? folder) => _pages.Remove(Key(connection, folder));

    public void RemoveConnection(Guid connection)
    {
        var prefix = Key(connection, null);
        _pages.Retain(key => !key.StartsWith(prefix, StringComparison.Ordinal));
    }

    public void RetainConnections(IEnumerable<Guid> connections)
    {
        var prefixes = connections.Select(connection => connection.ToString("N")).ToHashSet(StringComparer.Ordinal);
        _pages.Retain(key => prefixes.Contains(key[..32]));
    }

    public void Clear() => _pages.Clear();

    private static string Key(Guid connection, string? folder) => $"{connection:N}/{folder}";
}
