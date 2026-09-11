using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.Server.Installations;

public sealed record InstallationEntry(InstallationOperationDto Operation, string ActorReference,
    string IdempotencyReference, string RequestReference, string[] Resources);
public sealed record InstallationAudit(Guid OperationId, string ActorReference, InstallationServiceId Service,
    InstallationOperationKind Kind, InstallationOperationState State, InstallationStage Stage,
    string Event, string? ProblemCode, DateTimeOffset At, string[] Resources);

/// <summary>One atomic, secret-free ledger including audit events. Corruption blocks all new host mutations.</summary>
public sealed class InstallationOperationStore
{
    private readonly object gate = new();
    private readonly string path;
    private Ledger ledger = new([], []);
    private bool unavailable;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public InstallationOperationStore(IHostEnvironment environment)
    {
        path = Path.Combine(environment.ContentRootPath, "data", "installation-operations.json");
        try
        {
            if (File.Exists(path))
            {
                ledger = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(path), Json) ?? throw new JsonException();
                if (ledger.Entries is null || ledger.Audit is null || ledger.Entries.Select(x => x.Operation.OperationId).Distinct().Count() != ledger.Entries.Length
                    || ledger.Entries.Any(x => !Valid(x))) throw new JsonException();
            }
        }
        catch { unavailable = true; }
    }

    public static string Reference(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static bool Active(InstallationOperationDto operation) => operation.State is InstallationOperationState.Queued or InstallationOperationState.Running;

    public InstallationEntry[] Read()
    {
        lock (gate) { EnsureAvailable(); return ledger.Entries.ToArray(); }
    }

    public InstallationEntry Create(InstallationServiceId service, InstallationOperationKind kind, string actor,
        string key, string requestReference, IReadOnlyList<string> resources, out bool created)
    {
        lock (gate)
        {
            EnsureAvailable();
            var actorReference = Reference(actor);
            var keyReference = Reference(actorReference + "\n" + key);
            var existing = ledger.Entries.FirstOrDefault(x => x.IdempotencyReference == keyReference);
            if (existing is not null)
            {
                if (existing.Operation.Service != service || existing.Operation.Kind != kind || existing.RequestReference != requestReference)
                    throw new InstallationException(InstallationProblemCodes.IdempotencyConflict);
                created = false;
                return existing;
            }
            var locks = resources.Select(Reference).Distinct().Order().ToArray();
            if (ledger.Entries.Any(x => Active(x.Operation) && x.Resources.Intersect(locks).Any()))
                throw new InstallationException(InstallationProblemCodes.ResourceConflict);
            var entry = new InstallationEntry(new(Guid.NewGuid(), service, kind, InstallationOperationState.Queued,
                InstallationStage.Queued, null, null, DateTimeOffset.UtcNow, null, null, true), actorReference, keyReference, requestReference, locks);
            Commit(new([.. ledger.Entries, entry], [.. ledger.Audit, Audit(entry, "created")]));
            created = true;
            return entry;
        }
    }

    public InstallationOperationDto Update(Guid id, Func<InstallationOperationDto, InstallationOperationDto> update, string eventName)
    {
        lock (gate)
        {
            EnsureAvailable();
            var before = ledger.Entries.Single(x => x.Operation.OperationId == id);
            var after = before with { Operation = update(before.Operation) };
            if (!Valid(after)) throw new InstallationException(InstallationProblemCodes.InvalidRequest, 500);
            if (after == before) return before.Operation;
            var audit = before.Operation.State != after.Operation.State || before.Operation.Stage != after.Operation.Stage || eventName is "cancel-requested" or "recovered";
            Commit(new(ledger.Entries.Select(x => x.Operation.OperationId == id ? after : x).ToArray(),
                audit ? [.. ledger.Audit, Audit(after, eventName)] : ledger.Audit));
            return after.Operation;
        }
    }

    private static InstallationAudit Audit(InstallationEntry entry, string name) => new(entry.Operation.OperationId,
        entry.ActorReference, entry.Operation.Service, entry.Operation.Kind, entry.Operation.State, entry.Operation.Stage,
        name, entry.Operation.ProblemCode, DateTimeOffset.UtcNow, entry.Resources);

    private void Commit(Ledger next)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(path + ".tmp", path, overwrite: true);
            ledger = next;
        }
        catch { unavailable = true; throw new InstallationException(InstallationProblemCodes.StoreUnavailable, 503); }
    }

    private void EnsureAvailable()
    {
        if (unavailable) throw new InstallationException(InstallationProblemCodes.StoreUnavailable, 503);
    }

    private static bool Valid(InstallationEntry entry) => entry.Operation is { } op && op.OperationId != Guid.Empty
        && Enum.IsDefined(op.Service) && Enum.IsDefined(op.Kind) && Enum.IsDefined(op.State) && Enum.IsDefined(op.Stage)
        && (op.Progress is null or >= 0 and <= 100)
        && (op.Progress is null || op.Stage is InstallationStage.Downloading or InstallationStage.Copying or InstallationStage.Installing or InstallationStage.UpdatingPackageLists)
        && entry.ActorReference?.Length == 64 && entry.IdempotencyReference?.Length == 64 && entry.RequestReference?.Length == 64
        && entry.Resources is { Length: > 0 } && entry.Resources.All(x => x?.Length == 64)
        && (op.ProblemCode is null || op.ProblemCode.Length <= 120 && op.ProblemCode.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_'));
    private sealed record Ledger(InstallationEntry[] Entries, InstallationAudit[] Audit);
}
