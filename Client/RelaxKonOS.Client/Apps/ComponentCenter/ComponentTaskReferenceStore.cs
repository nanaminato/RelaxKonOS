using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

/// <summary>Public plan/operation references only: never passwords, bearer tokens, grants or callback channels.</summary>
public sealed record ComponentTaskReference([property: JsonRequired] TaskPlan Plan, [property: JsonRequired] Guid ApprovalId,
    [property: JsonRequired] string StartKey, [property: JsonRequired] Guid? RunId);

public sealed class ComponentTaskReferenceStore(string directory)
{
    private readonly string _directory = Path.GetFullPath(directory);
    private static readonly JsonSerializerOptions Json = new(RelaxKonOSJsonOptions.Default)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    public IReadOnlyList<ComponentTaskReference> Read(string server, Guid actor)
    {
        if (File.Exists(_directory)) throw new IOException("task.reference_invalid");
        var scope = Scope(server, actor);
        if (!Directory.Exists(scope)) return [];
        var files = Directory.EnumerateFiles(scope, "*.json").Take(65).ToArray();
        if (files.Length > 64) throw new InvalidDataException("task.reference_capacity_exceeded");
        return files.Select(path => ReadFile(path, server, actor)).OrderByDescending(item => item.Plan.CreatedAt).ToArray();
    }
    public void Save(string server, Guid actor, ComponentTaskReference reference)
    {
        Validate(reference, actor);
        var scope = Scope(server, actor); Directory.CreateDirectory(scope);
        // Serialize writers across desktop processes. A busy or unavailable store never permits a caller to start blindly.
        using var writer = new FileStream(Path.Combine(scope, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var existing = Read(server, actor);
        var previous = existing.SingleOrDefault(item => item.ApprovalId == reference.ApprovalId);
        if (previous is null && existing.Count >= 64) throw new InvalidDataException("task.reference_capacity_exceeded");
        if (previous is not null && (previous.Plan.PlanId != reference.Plan.PlanId || previous.Plan.PlanDigest != reference.Plan.PlanDigest
            || previous.StartKey != reference.StartKey || previous.RunId is { } runId && reference.RunId != runId))
            throw new InvalidDataException("task.reference_conflict");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, server, actor, reference), Json);
        if (bytes.Length > 524288) throw new InvalidDataException("task.reference_invalid");
        var path = Path.Combine(scope, reference.ApprovalId.ToString("D") + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private string Scope(string server, Guid actor)
    {
        if (string.IsNullOrWhiteSpace(server) || server.Length > 2048 || actor == Guid.Empty)
            throw new ArgumentException("task.reference_scope_invalid");
        return Path.Combine(_directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { server, actor })))));
    }
    private static ComponentTaskReference ReadFile(string path, string server, Guid actor)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 524288) throw new InvalidDataException("task.reference_invalid");
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
            TaskPlanCanonicalizer.Canonicalize(document.RootElement);
            var envelope = document.RootElement.Deserialize<Envelope>(Json) ?? throw new JsonException();
            if (envelope.Schema != 1 || envelope.Server != server || envelope.Actor != actor) throw new JsonException();
            Validate(envelope.Reference, actor);
            if (Path.GetFileName(path) != envelope.Reference.ApprovalId.ToString("D") + ".json") throw new JsonException();
            return envelope.Reference;
        }
        catch (Exception ex) when (ex is JsonException or TaskPlanningException or NullReferenceException or ArgumentException)
        { throw new InvalidDataException("task.reference_invalid", ex); }
    }
    private static void Validate(ComponentTaskReference reference, Guid actor)
    {
        try
        {
            if (reference is null || reference.Plan is not { Semantics: { } semantics } plan || plan.PlanId == Guid.Empty
                || reference.ApprovalId == Guid.Empty || reference.RunId == Guid.Empty || semantics.CanonicalSubject != actor.ToString("D")
                || string.IsNullOrWhiteSpace(semantics.HostId) || semantics.Steps.IsDefaultOrEmpty
                || TaskPlanCanonicalizer.Digest(semantics) != plan.PlanDigest || TaskPlanCanonicalizer.Serialize(semantics).Length > TaskPlanLimits.Default.MaximumPlanBytes
                || string.IsNullOrWhiteSpace(reference.StartKey) || reference.StartKey.Length > 128 || reference.StartKey.Any(c => c < 33 || c > 126))
                throw new InvalidDataException("task.reference_invalid");
        }
        catch (Exception ex) when (ex is TaskPlanningException or NullReferenceException or ArgumentException or InvalidOperationException)
        { throw new InvalidDataException("task.reference_invalid", ex); }
    }
    private sealed record Envelope([property: JsonRequired] int Schema, [property: JsonRequired] string Server,
        [property: JsonRequired] Guid Actor, [property: JsonRequired] ComponentTaskReference Reference);
}
