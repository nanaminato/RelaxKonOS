using System.Text.Json;
using RelaxKonOS.Client.Services.HostSettings;

namespace RelaxKonOS.Client.Apps.Tailscale;

public interface ITailscaleOperationReferences
{
    IReadOnlyList<Guid> Read(HostSettingsConnection connection);
    void Remember(HostSettingsConnection connection, Guid operationId);
}

/// <summary>Device-local host/user-scoped references only. The Server owns operation outcomes.</summary>
public sealed class TailscaleOperationReferences : ITailscaleOperationReferences
{
    private readonly string _path;
    private readonly object _gate = new();
    public TailscaleOperationReferences() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RelaxKonOS", "tailscale-operations.json")) { }
    public TailscaleOperationReferences(string path) => _path = path;
    public IReadOnlyList<Guid> Read(HostSettingsConnection connection)
    {
        lock (_gate) return Load().Where(x => x.Host == connection.ServiceId && x.User == connection.UserId).Select(x => x.Operation).ToArray();
    }
    public void Remember(HostSettingsConnection connection, Guid operationId)
    {
        if (operationId == Guid.Empty) throw new InvalidDataException("tailscale.operation_reference_invalid");
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            using var gate = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var references = Load().Where(x => !(x.Host == connection.ServiceId && x.User == connection.UserId && x.Operation == operationId)).ToList();
            references.Insert(0, new(connection.ServiceId, connection.UserId, operationId));
            // Keep the newest 30 references per account and at most 256 across this device.
            var saved = references.GroupBy(x => (x.Host, x.User)).SelectMany(x => x.Take(30)).Take(256).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, saved); stream.Flush(true); }
                File.Move(temporary, _path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    private Reference[] Load()
    {
        if (!File.Exists(_path)) return [];
        if (new FileInfo(_path).Length > 262144) throw new InvalidDataException("tailscale.operation_reference_invalid");
        var references = JsonSerializer.Deserialize<Reference[]>(File.ReadAllBytes(_path)) ?? throw new InvalidDataException("tailscale.operation_reference_invalid");
        if (references.Length > 256 || references.Any(x => x is null || x.Operation == Guid.Empty || x.User == Guid.Empty || x.Host is not { Length: > 0 and <= 2048 }))
            throw new InvalidDataException("tailscale.operation_reference_invalid");
        return references;
    }
    private sealed record Reference(string Host, Guid User, Guid Operation);
}
