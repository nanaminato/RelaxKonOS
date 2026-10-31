using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Services.Modules;

/// <summary>Public unresolved plan references only, scoped by saved connection and account.</summary>
public sealed class ServerModuleReferenceStore(string directory)
{
    private sealed record Reference(string ConnectionId, Guid Actor, ServerModulePlanDto Plan);
    private string Scope(string connectionId, Guid actor)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || actor == Guid.Empty) throw new InvalidDataException("module-reference-invalid");
        return Path.Combine(Path.GetFullPath(directory), Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { connectionId, actor })))));
    }
    public IReadOnlyList<ServerModulePlanDto> Read(string connectionId, Guid actor)
    {
        var scope = Scope(connectionId, actor);
        if (!Directory.Exists(scope)) return [];
        var paths = Directory.EnumerateFiles(scope, "*.json").Take(65).ToArray();
        if (paths.Length > 64) throw new InvalidDataException("module-reference-capacity");
        return paths.Select(path =>
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 65536) throw new InvalidDataException("module-reference-invalid");
            var reference = JsonSerializer.Deserialize<Reference>(stream, RelaxKonOSJsonOptions.Default);
            if (reference is null || reference.ConnectionId != connectionId || reference.Actor != actor
                || reference.Plan is null || reference.Plan.PlanId == Guid.Empty
                || reference.Plan.ServiceId != reference.Plan.Target.ServiceId
                || Path.GetFileName(path) != reference.Plan.PlanId.ToString("N") + ".json")
                throw new InvalidDataException("module-reference-invalid");
            return reference.Plan;
        }).ToArray();
    }
    public void Save(string connectionId, Guid actor, ServerModulePlanDto plan)
    {
        var scope = Scope(connectionId, actor); Directory.CreateDirectory(scope);
        using var writer = new FileStream(Path.Combine(scope, "writer.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var previous = Read(connectionId, actor);
        var existing = previous.SingleOrDefault(x => x.PlanId == plan.PlanId);
        if (existing is not null)
        {
            if (JsonSerializer.Serialize(existing, RelaxKonOSJsonOptions.Default) != JsonSerializer.Serialize(plan, RelaxKonOSJsonOptions.Default))
                throw new InvalidDataException("module-reference-conflict");
            return;
        }
        if (previous.Count >= 64) throw new InvalidDataException("module-reference-capacity");
        var path = Path.Combine(scope, plan.PlanId.ToString("N") + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, new Reference(connectionId, actor, plan), RelaxKonOSJsonOptions.Default); stream.Flush(true); }
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Resolve(string connectionId, Guid actor, Guid planId) => File.Delete(Path.Combine(Scope(connectionId, actor), planId.ToString("N") + ".json"));
}
