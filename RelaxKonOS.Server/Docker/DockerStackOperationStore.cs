using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.Docker;

internal sealed record StackOperationEntry(
    DockerStackOperationDto Operation,
    string ActorReference,
    string IdempotencyReference,
    string RequestReference,
    string ProjectReference,
    string[]? Diagnostics = null,
    bool DiagnosticsTruncated = false);

internal sealed record StackAudit(
    Guid OperationId,
    string ActorReference,
    string ProjectName,
    DockerStackOperationKind Kind,
    DockerStackOperationState State,
    DockerStackOperationStage Stage,
    string Event,
    string? ProblemCode,
    DateTimeOffset At);

/// <summary>
/// Durable, secret-free ledger of Compose stack operations. It is the authority for what happened to a
/// project, for idempotent retries, and for serializing work per project while other projects keep
/// running. Compose errors can echo substituted environment values, so output is sanitized and bounded
/// at this boundary and nothing unsanitized is ever written.
/// </summary>
internal sealed class DockerStackOperationStore
{
    /// <summary>A terminal operation is history; the ledger keeps the most recent ones and never drops an
    /// active one, so trimming cannot lose work that a startup reconciliation still has to reason about.</summary>
    private const int RetainedOperations = 200;
    private const int RetainedAuditRecords = 1000;
    private const int MaximumDiagnosticsLines = 120;
    /// <summary>Per-line bound, matching the application-deployment log store so both domains sanitize
    /// the same way.</summary>
    private const int MaximumDiagnosticsLineLength = 512;

    private readonly object gate = new();
    private readonly string path;
    private Ledger ledger = new([], []);
    private bool unavailable;

    private static readonly JsonSerializerOptions Json = new(RelaxKonOSJsonOptions.Default)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public DockerStackOperationStore(IHostEnvironment environment, IOptions<DockerComposeOptions> options)
    {
        path = DockerComposePaths.LedgerPath(environment, options.Value.DataDirectory);
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

    public static bool Active(DockerStackOperationDto operation) =>
        operation.State is DockerStackOperationState.Queued or DockerStackOperationState.Running;

    public StackOperationEntry[] Read()
    {
        lock (gate) { EnsureAvailable(); return [.. ledger.Entries]; }
    }

    /// <summary>Most recent operations of one project, newest first. The project name is matched
    /// case-sensitively because Compose project names are case-sensitive resource identifiers.</summary>
    public StackOperationEntry[] History(string project, int maximum)
    {
        lock (gate)
        {
            EnsureAvailable();
            return [.. ledger.Entries.Where(x => x.Operation.ProjectName == project)
                .OrderByDescending(x => x.Operation.CreatedAt)
                .Take(Math.Clamp(maximum, 1, 100))];
        }
    }

    public StackOperationEntry? Get(Guid operationId) => Read().FirstOrDefault(x => x.Operation.OperationId == operationId);

    public StackOperationEntry? GetActive(string project) => Read()
        .Where(x => x.Operation.ProjectName == project && Active(x.Operation))
        .OrderByDescending(x => x.Operation.CreatedAt)
        .FirstOrDefault();

    public string[] Diagnostics(Guid operationId)
    {
        var entry = Get(operationId) ?? throw new DockerStackException(DockerStackProblem.OperationNotFound, 404);
        return entry.Diagnostics ?? [];
    }

    public bool DiagnosticsTruncated(Guid operationId) => Get(operationId)?.DiagnosticsTruncated ?? false;

    /// <summary>Looks up a replay before admission control, so a retried request still returns its
    /// original operation while the concurrency ceiling happens to be saturated.</summary>
    public StackOperationEntry? FindIdempotent(DockerStackOperationKind kind, string actor, string key, string requestReference)
    {
        lock (gate)
        {
            EnsureAvailable();
            var existing = Find(key, actor);
            if (existing is null) return null;
            if (existing.Operation.Kind != kind || existing.RequestReference != requestReference)
                throw new DockerStackException(DockerStackProblem.IdempotencyConflict);
            return existing;
        }
    }

    /// <summary>
    /// Creates the operation record or returns the one already bound to this idempotency key. The same
    /// key with a different request, or any active change to the same project, is rejected instead of
    /// being silently merged.
    /// </summary>
    public StackOperationEntry Create(string project, DockerStackOperationKind kind, string actor, string key,
        string requestReference, out bool created)
    {
        lock (gate)
        {
            EnsureAvailable();
            var existing = Find(key, actor);
            if (existing is not null)
            {
                if (existing.Operation.Kind != kind || existing.RequestReference != requestReference)
                    throw new DockerStackException(DockerStackProblem.IdempotencyConflict);
                created = false;
                return existing;
            }

            var projectReference = DockerStackValidation.Reference(project);
            if (ledger.Entries.Any(x => Active(x.Operation) && x.ProjectReference == projectReference))
                throw new DockerStackException(DockerStackProblem.OperationConflict);

            var entry = new StackOperationEntry(
                new(Guid.NewGuid(), project, kind, DockerStackOperationState.Queued, DockerStackOperationStage.Queued,
                    null, null, DockerStackValidation.Reference(actor), DateTimeOffset.UtcNow, null, null, true, []),
                DockerStackValidation.Reference(actor),
                DockerStackValidation.Reference(DockerStackValidation.Reference(actor) + "\n" + key),
                requestReference, projectReference);
            Commit(new([.. ledger.Entries, entry], [.. ledger.Audit, Audit(entry, "created")]));
            created = true;
            return entry;
        }
    }

    public DockerStackOperationDto Update(Guid operationId, Func<DockerStackOperationDto, DockerStackOperationDto> update,
        string eventName, IReadOnlyList<string>? diagnostics = null, bool diagnosticsTruncated = false)
    {
        lock (gate)
        {
            EnsureAvailable();
            var before = ledger.Entries.Single(x => x.Operation.OperationId == operationId);
            var after = before with { Operation = update(before.Operation) };
            if (diagnostics is { Count: > 0 })
            {
                var bounded = BoundDiagnostics(diagnostics, diagnosticsTruncated);
                after = after with { Diagnostics = bounded.Lines, DiagnosticsTruncated = bounded.Truncated };
            }
            if (!Valid(after)) throw new DockerStackException(DockerStackProblem.ValidationFailed, 500);
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

    private StackOperationEntry? Find(string key, string actor)
    {
        var keyReference = DockerStackValidation.Reference(DockerStackValidation.Reference(actor) + "\n" + key);
        return ledger.Entries.FirstOrDefault(x => x.IdempotencyReference == keyReference);
    }

    /// <summary>A line that sanitizes away entirely is dropped rather than stored blank, and a dropped
    /// head is recorded instead of implied, so a reader never mistakes a tail for the complete log.</summary>
    private static (string[]? Lines, bool Truncated) BoundDiagnostics(IReadOnlyList<string> diagnostics, bool headDropped)
    {
        // The lambda is deliberate: `Select` also has a `Func<T, int, TResult>` overload, so passing
        // `ProxyLogSanitizer.Sanitize` as a method group would bind this line's *index* to its maximum
        // length and truncate the head of every log to nothing.
        var lines = diagnostics.Select(line => SanitizeLine(line)).Where(line => line.Length > 0).ToArray();
        return lines.Length == 0
            ? (null, false)
            : ([.. lines.TakeLast(MaximumDiagnosticsLines)], headDropped || lines.Length > MaximumDiagnosticsLines);
    }

    private static string SanitizeLine(string? line) => ProxyLogSanitizer.Sanitize(line, MaximumDiagnosticsLineLength);

    private static StackAudit Audit(StackOperationEntry entry, string name) => new(entry.Operation.OperationId,
        entry.ActorReference, entry.Operation.ProjectName, entry.Operation.Kind, entry.Operation.State,
        entry.Operation.Stage, name, entry.Operation.ProblemCode, DateTimeOffset.UtcNow);

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
            throw new DockerStackException(DockerStackProblem.StoreUnavailable, 503);
        }
    }

    /// <summary>Keeps the ledger bounded by retiring the oldest terminal operations first; audit records
    /// are filtered afterwards so a row can never reference an operation that is no longer present.</summary>
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
        if (unavailable) throw new DockerStackException(DockerStackProblem.StoreUnavailable, 503);
    }

    private static bool Valid(StackOperationEntry entry) => entry.Operation is { } operation
        && operation.OperationId != Guid.Empty
        && DockerStackValidation.IsValidProjectName(operation.ProjectName)
        && Enum.IsDefined(operation.Kind) && Enum.IsDefined(operation.State) && Enum.IsDefined(operation.Stage)
        && DockerStackValidation.IsValidProblemCode(operation.ProblemCode)
        && DockerStackValidation.IsValidProblemCode(operation.RecoveryProblemCode)
        && DockerStackValidation.IsValidReference(operation.RequestedByReference)
        && operation.CreatedAt != default
        && operation.Services is not null
        && operation.Services.Count <= 64
        && operation.Services.All(service => service.Service?.Length <= 63 && service.Container?.Length <= 128
            && service.Image?.Length <= 255 && service.State?.Length <= 32 && service.Status?.Length <= 256)
        && entry.ActorReference?.Length == 64
        && entry.IdempotencyReference?.Length == 64
        && entry.RequestReference?.Length == 64
        && entry.ProjectReference?.Length == 64
        && entry.Diagnostics is null or { Length: > 0 and <= MaximumDiagnosticsLines }
        && (entry.Diagnostics is not null || !entry.DiagnosticsTruncated);

    private static bool Valid(StackAudit audit, IReadOnlyCollection<StackOperationEntry> entries) => audit.OperationId != Guid.Empty
        && entries.Any(x => x.Operation.OperationId == audit.OperationId)
        && audit.ActorReference?.Length == 64
        && !string.IsNullOrWhiteSpace(audit.ProjectName) && audit.ProjectName.Length <= 63
        && Enum.IsDefined(audit.Kind) && Enum.IsDefined(audit.State) && Enum.IsDefined(audit.Stage)
        && !string.IsNullOrWhiteSpace(audit.Event) && audit.Event.Length <= 80
        && audit.Event.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
        && DockerStackValidation.IsValidProblemCode(audit.ProblemCode);

    private sealed record Ledger(StackOperationEntry[] Entries, StackAudit[] Audit);
}
