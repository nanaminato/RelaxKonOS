using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

/// <summary>Local intent, isolated by server and actor. Never stores plans or authorization.</summary>
public sealed class ComponentDraftStore(string? directory = null)
{
    private readonly Dictionary<(string Server, Guid Actor), ComponentChangeDraft[]> _drafts = [];
    private readonly HashSet<(string Server, Guid Actor)> _failures = [];
    private string FilePath(string server, Guid actor) => Path.Combine(directory!,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { server, actor })))) + ".json");

    public bool HasPersistenceFailure(string server, Guid actor) => _failures.Contains((server, actor));

    public IReadOnlyList<ComponentChangeDraft> Read(string server, Guid actor)
    {
        var key = (server, actor);
        if (_drafts.TryGetValue(key, out var cached)) return cached.ToArray();
        ComponentChangeDraft[] items = [];
        if (directory is not null)
        {
            try
            {
                using var stream = File.OpenRead(FilePath(server, actor));
                if (stream.Length > 1024 * 1024) throw new JsonException();
                items = JsonSerializer.Deserialize<ComponentChangeDraft[]>(stream) ?? throw new JsonException();
                if (!Valid(items)) throw new JsonException();
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { items = []; _failures.Add(key); }
        }
        _drafts[key] = items;
        return items.ToArray();
    }

    public void Save(string server, Guid actor, IEnumerable<ComponentChangeDraft> drafts)
    {
        var items = drafts.ToArray();
        if (!Valid(items)) throw new ArgumentException("Invalid component draft.", nameof(drafts));
        var key = (server, actor);
        _drafts[key] = items;
        if (directory is null) return;
        var path = FilePath(server, actor);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (items.Length == 0) File.Delete(path);
            else
            {
                Directory.CreateDirectory(directory);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, items);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            _failures.Remove(key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _failures.Add(key); }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool Valid(ComponentChangeDraft[] items) => items.Length <= ComponentCatalog.Components.Count
        && items.All(item => item is not null && ComponentCatalog.Find(item.ComponentId) is { } component
            && component.RuntimeId == item.RuntimeId && !string.IsNullOrWhiteSpace(item.HostId)
            && item.InstallationId != Guid.Empty && Enum.IsDefined(item.Change)
            && (item.Change == RelaxKonOS.Protocol.ManagedRuntimes.RuntimeChange.Uninstall
                || (!string.IsNullOrWhiteSpace(item.SourceId) && !string.IsNullOrWhiteSpace(item.ReleaseId))))
        && items.Select(item => item.ComponentId).Distinct(StringComparer.Ordinal).Count() == items.Length;
}
