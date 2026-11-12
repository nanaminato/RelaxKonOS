using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Models;

/// <summary>Per-connection navigation only; never share provider references across accounts.</summary>
internal sealed class CloudAccountNavigation
{
    private readonly Dictionary<Guid, CloudEntryDto[]> _folders = [];

    public CloudEntryDto[] Get(Guid connection) =>
        _folders.TryGetValue(connection, out var folders) ? folders.ToArray() : [];

    public void Save(Guid connection, IEnumerable<CloudEntryDto> folders) =>
        _folders[connection] = folders.ToArray();

    public void Retain(IEnumerable<Guid> connections)
    {
        var live = connections.ToHashSet();
        foreach (var id in _folders.Keys.Where(id => !live.Contains(id)).ToArray()) _folders.Remove(id);
    }

    public void Clear() => _folders.Clear();
}
