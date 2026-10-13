using System.Text.Json;
using System.Text.Json.Nodes;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Registry;

namespace RelaxKonOS.Server.Settings;

/// <summary>Instance-owned settings, durably committed before runtime consumers observe them.</summary>
public sealed class ServerRuntimeSettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private StoredDocument _document;
    public event Action? Changed;
    public ServerRuntimeSnapshot Current { get { lock (_gate) return _document.Current; } }
    public ServerRuntimeSettingsStore(string path)
    {
        _path = Path.GetFullPath(path);
        if (File.Exists(_path))
        {
            _document = JsonSerializer.Deserialize<StoredDocument>(File.ReadAllText(_path), RelaxKonOSJsonOptions.Default)
                ?? throw new InvalidDataException("Invalid server runtime settings.");
            if (!_document.Current.Settings.IsValid || _document.Current.InstanceId == Guid.Empty || _document.Current.Revision < 1)
                throw new InvalidDataException("Invalid server runtime settings.");
        }
        else
        {
            _document = new(new(Guid.NewGuid(), 1, ServerRuntimeSettings.Default, null, DateTimeOffset.UtcNow), []);
            Persist(_document);
        }
    }
    public ServerRuntimeSnapshot? Save(PutServerRuntimeFieldRequest request, Guid actor)
    {
        ServerRuntimeSnapshot saved;
        lock (_gate)
        {
            if (_document.Current.InstanceId != request.InstanceId || _document.Current.Revision != request.ExpectedRevision) return null;
            var json = (JsonObject)JsonSerializer.SerializeToNode(_document.Current.Settings, RelaxKonOSJsonOptions.Default)!;
            if (!json.ContainsKey(request.Name) || request.Value.ValueKind != JsonValueKind.Number || !request.Value.TryGetInt32(out _))
                throw new ArgumentException("Invalid server runtime field.");
            json[request.Name] = JsonNode.Parse(request.Value.GetRawText());
            var settings = json.Deserialize<ServerRuntimeSettings>(RelaxKonOSJsonOptions.Default)!;
            if (!settings.IsValid) throw new ArgumentException("Invalid server runtime setting range.");
            saved = new(_document.Current.InstanceId, _document.Current.Revision + 1, settings, actor, DateTimeOffset.UtcNow);
            var audit = new Audit(actor, saved.UpdatedAt, _document.Current.Settings, settings, saved.Revision);
            var next = new StoredDocument(saved, [.. _document.History.TakeLast(63), audit]);
            Persist(next);
            _document = next;
        }
        Changed?.Invoke();
        return saved;
    }
    private void Persist(StoredDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, RelaxKonOSJsonOptions.Default);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public sealed record Audit(Guid Actor, DateTimeOffset At, ServerRuntimeSettings Before, ServerRuntimeSettings After, long Revision);
    public sealed record StoredDocument(ServerRuntimeSnapshot Current, Audit[] History);
}
