using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Server.ApplicationDeployments;

internal sealed record DeploymentEntry(
    DeploymentOperationDto Operation,
    string ActorReference,
    string IdempotencyReference,
    string RequestReference,
    string[] Resources);

internal sealed record DeploymentAudit(
    Guid OperationId,
    string ActorReference,
    Guid ApplicationId,
    DeploymentOperationKind Kind,
    DeploymentOperationState State,
    DeploymentStage Stage,
    string Event,
    string? ProblemCode,
    DateTimeOffset At,
    string[] Resources);

/// <summary>
/// Durable, secret-free operation ledger including its audit trail. It is also the authority for
/// idempotency and for serializing changes to the same application while allowing different
/// applications to proceed in parallel.
/// </summary>
internal sealed class ApplicationDeploymentOperationStore
{
    /// <summary>A terminal operation is history; the ledger keeps the most recent ones and never drops
    /// an active one, so trimming cannot lose work a startup reconciliation still has to reason about.</summary>
    private const int RetainedOperations = 500;
    private const int RetainedAuditRecords = 2000;

    private readonly object gate = new();
    private readonly string path;
    private Ledger ledger = new([], []);
    private bool unavailable;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public ApplicationDeploymentOperationStore(IHostEnvironment environment, ApplicationDeploymentOptions options)
    {
        var root = Path.Combine(environment.ContentRootPath, options.RootDirectory);
        path = Path.Combine(root, "operations.json");
        try
        {
            if (!File.Exists(path)) return;
            ledger = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(path), Json) ?? throw new JsonException();
            if (ledger.Entries is null || ledger.Audit is null
                || ledger.Entries.Select(x => x.Operation.OperationId).Distinct().Count() != ledger.Entries.Length
                || ledger.Entries.Any(x => !Valid(x))
                || ledger.Audit.Any(x => !Valid(x, ledger.Entries))) throw new JsonException();
        }
        catch { unavailable = true; }
    }

    public static string Reference(string value) => ApplicationDeploymentValidation.Reference(value);
    public static bool Active(DeploymentOperationDto operation) =>
        operation.State is DeploymentOperationState.Queued or DeploymentOperationState.Running;

    public DeploymentEntry[] Read()
    {
        lock (gate) { EnsureAvailable(); return [.. ledger.Entries]; }
    }

    public DeploymentAudit[] ReadAudit(Guid applicationId, int maximum)
    {
        lock (gate)
        {
            EnsureAvailable();
            return [.. ledger.Audit.Where(x => x.ApplicationId == applicationId).OrderByDescending(x => x.At).Take(Math.Clamp(maximum, 1, 500))];
        }
    }

    public DeploymentEntry? Get(Guid operationId) => Read().FirstOrDefault(x => x.Operation.OperationId == operationId);

    public DeploymentEntry? GetActive(Guid applicationId) => Read()
        .Where(x => x.Operation.ApplicationId == applicationId && Active(x.Operation))
        .OrderByDescending(x => x.Operation.CreatedAt)
        .FirstOrDefault();

    /// <summary>Looks up a replay before admission control. A retry must return its original
    /// operation even when the global worker limit is currently saturated.</summary>
    public DeploymentEntry? FindIdempotent(Guid applicationId, DeploymentOperationKind kind, string actor, string key, string requestReference)
    {
        lock (gate)
        {
            EnsureAvailable();
            var actorReference = Reference(actor);
            var keyReference = Reference(actorReference + "\n" + key);
            var existing = ledger.Entries.FirstOrDefault(x => x.IdempotencyReference == keyReference);
            if (existing is null) return null;
            if (existing.Operation.ApplicationId != applicationId || existing.Operation.Kind != kind
                || existing.RequestReference != requestReference)
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.IdempotencyConflict);
            return existing;
        }
    }

    /// <summary>
    /// Creates the operation record or returns the one already bound to this idempotency key.
    /// The same key with a different request, or any active change to the same application,
    /// is rejected instead of being silently merged.
    /// </summary>
    public DeploymentEntry Create(Guid applicationId, string applicationName, DeploymentOperationKind kind, string actor,
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
                if (existing.Operation.ApplicationId != applicationId || existing.Operation.Kind != kind
                    || existing.RequestReference != requestReference)
                    throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.IdempotencyConflict);
                created = false;
                return existing;
            }

            var locks = resources.Select(Reference).Distinct().Order(StringComparer.Ordinal).ToArray();
            if (ledger.Entries.Any(x => Active(x.Operation) && x.Resources.Intersect(locks, StringComparer.Ordinal).Any()))
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ResourceConflict);

            var entry = new DeploymentEntry(
                new(Guid.NewGuid(), applicationId, applicationName, kind, null, null,
                    DeploymentOperationState.Queued, DeploymentStage.Queued, null, null, null,
                    actorReference, DateTimeOffset.UtcNow, null, null, true),
                actorReference, keyReference, requestReference, locks);
            Commit(new([.. ledger.Entries, entry], [.. ledger.Audit, Audit(entry, "created")]));
            created = true;
            return entry;
        }
    }

    public DeploymentOperationDto Update(Guid operationId, Func<DeploymentOperationDto, DeploymentOperationDto> update, string eventName)
    {
        lock (gate)
        {
            EnsureAvailable();
            var before = ledger.Entries.Single(x => x.Operation.OperationId == operationId);
            var after = before with { Operation = update(before.Operation) };
            if (!Valid(after)) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 500);
            if (after == before) return before.Operation;
            var audit = after.Operation.State != before.Operation.State
                || after.Operation.Stage != before.Operation.Stage
                || eventName is "cancel-requested" or "recovered" or "completed" or "failed";
            Commit(new(
                [.. ledger.Entries.Select(x => x.Operation.OperationId == operationId ? after : x)],
                audit ? [.. ledger.Audit, Audit(after, eventName)] : ledger.Audit));
            return after.Operation;
        }
    }

    private static DeploymentAudit Audit(DeploymentEntry entry, string name) => new(entry.Operation.OperationId,
        entry.ActorReference, entry.Operation.ApplicationId, entry.Operation.Kind, entry.Operation.State,
        entry.Operation.Stage, name, entry.Operation.ProblemCode, DateTimeOffset.UtcNow, entry.Resources);

    private void Commit(Ledger next)
    {
        try
        {
            var bounded = Bound(next);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(bounded, Json);
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(path + ".tmp", path, overwrite: true);
            ledger = bounded;
        }
        catch
        {
            unavailable = true;
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.StoreUnavailable, 503);
        }
    }

    /// <summary>
    /// Keeps the ledger bounded by retiring the oldest terminal operations first. Audit records are
    /// filtered afterwards so an audit row can never reference an operation that is no longer present,
    /// which is the invariant the constructor validates when it loads the file.
    /// </summary>
    private static Ledger Bound(Ledger next)
    {
        var entries = next.Entries;
        if (entries.Length > RetainedOperations)
        {
            var active = entries.Where(x => Active(x.Operation)).ToArray();
            var retired = entries.Where(x => !Active(x.Operation))
                .OrderByDescending(x => x.Operation.CreatedAt)
                .Take(Math.Max(0, RetainedOperations - active.Length))
                .ToArray();
            entries = [.. active.Concat(retired).OrderBy(x => x.Operation.CreatedAt)];
        }

        var identifiers = entries.Select(x => x.Operation.OperationId).ToHashSet();
        var audit = next.Audit.Where(x => identifiers.Contains(x.OperationId)).ToArray();
        if (audit.Length > RetainedAuditRecords)
            audit = [.. audit.OrderByDescending(x => x.At).Take(RetainedAuditRecords).OrderBy(x => x.At)];
        return new(entries, audit);
    }

    private void EnsureAvailable()
    {
        if (unavailable) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.StoreUnavailable, 503);
    }

    private static bool Valid(DeploymentEntry entry) => entry.Operation is { } operation
        && operation.OperationId != Guid.Empty
        && operation.ApplicationId != Guid.Empty
        && ApplicationDeploymentValidation.IsValidName(operation.ApplicationName)
        && Enum.IsDefined(operation.Kind) && Enum.IsDefined(operation.State) && Enum.IsDefined(operation.Stage)
        && (operation.Progress is null or >= 0 and <= 100)
        && (operation.Progress is null || operation.Stage is DeploymentStage.Pulling or DeploymentStage.Building
            or DeploymentStage.Preparing or DeploymentStage.HealthChecking)
        && (operation.TargetRevisionId is null || operation.TargetRevisionId != Guid.Empty)
        && ApplicationDeploymentValidation.IsValidProblemCode(operation.ProblemCode)
        && ApplicationDeploymentValidation.IsValidProblemCode(operation.RecoveryProblemCode)
        && ApplicationDeploymentValidation.IsValidReference(operation.RequestedByReference, 64)
        && operation.CreatedAt != default
        && entry.ActorReference?.Length == 64
        && entry.IdempotencyReference?.Length == 64
        && entry.RequestReference?.Length == 64
        && entry.Resources is { Length: > 0 } && entry.Resources.All(x => x?.Length == 64);

    private static bool Valid(DeploymentAudit audit, IReadOnlyCollection<DeploymentEntry> entries) => audit.OperationId != Guid.Empty
        && entries.Any(x => x.Operation.OperationId == audit.OperationId)
        && audit.ActorReference?.Length == 64
        && audit.ApplicationId != Guid.Empty
        && Enum.IsDefined(audit.Kind) && Enum.IsDefined(audit.State) && Enum.IsDefined(audit.Stage)
        && !string.IsNullOrWhiteSpace(audit.Event) && audit.Event.Length <= 80 && audit.Event.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
        && ApplicationDeploymentValidation.IsValidProblemCode(audit.ProblemCode)
        && audit.Resources is { Length: > 0 } && audit.Resources.All(x => x?.Length == 64);

    private sealed record Ledger(DeploymentEntry[] Entries, DeploymentAudit[] Audit);
}
