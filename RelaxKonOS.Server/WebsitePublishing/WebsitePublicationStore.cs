using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.WebsitePublishing;

namespace RelaxKonOS.Server.WebsitePublishing;

internal sealed class WebsitePublicationException(string problemCode, int statusCode = 409) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
    public int StatusCode { get; } = statusCode;
}

/// <summary>Crash-safe, secret-free authority for website-publishing work. Active work is never replayed on restart.</summary>
internal sealed class WebsitePublicationStore
{
    private const int RetainedOperations = 200;
    private readonly object gate = new();
    private readonly string path;
    private readonly Dictionary<Guid, Entry> entries = [];
    private bool unavailable;
    private static readonly JsonSerializerOptions Json = new(RelaxKonOSJsonOptions.Default)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal sealed record Entry(WebsitePublicationOperationDto Operation, string ActorReference, string IdempotencyReference, string RequestReference);

    public WebsitePublicationStore(IHostEnvironment environment)
    {
        path = Path.Combine(environment.ContentRootPath, "data", "website-publications.json");
        try
        {
            if (!File.Exists(path)) return;
            foreach (var entry in JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(path), Json) ?? [])
            {
                if (!Valid(entry) || !entries.TryAdd(entry.Operation.OperationId, entry)) throw new JsonException();
            }
        }
        catch { unavailable = true; }
    }

    public static bool Active(WebsitePublicationOperationDto operation) => operation.State is WebsitePublicationState.Queued or WebsitePublicationState.Running;

    public WebsitePublicationOperationDto Start(PublishWebsiteRequest request, string actor, string idempotencyKey, out bool created)
    {
        lock (gate)
        {
            EnsureAvailable();
            var actorReference = Reference(actor);
            var idempotencyReference = Reference(actorReference + "\n" + idempotencyKey);
            var requestReference = Reference(RequestIdentity(request));
            var existing = entries.Values.FirstOrDefault(entry => entry.IdempotencyReference == idempotencyReference);
            if (existing is not null)
            {
                if (existing.RequestReference != requestReference) throw new WebsitePublicationException("website.idempotency_conflict", 400);
                created = false;
                return existing.Operation;
            }
            if (entries.Values.Any(entry => Active(entry.Operation) && entry.Operation.ApplicationId == request.ApplicationId))
                throw new WebsitePublicationException("website.operation_conflict");

            var now = DateTimeOffset.UtcNow;
            var operation = new WebsitePublicationOperationDto(Guid.NewGuid(), request.ApplicationId, request.WebServerId, request.Domain,
                null, request.CertificateId, null, WebsitePublicationState.Queued, WebsitePublicationStage.Queued, "", null, [], now, null, null);
            entries.Add(operation.OperationId, new Entry(operation, actorReference, idempotencyReference, requestReference));
            Commit();
            created = true;
            return operation;
        }
    }

    public WebsitePublicationOperationDto? Get(Guid operationId)
    {
        lock (gate) { EnsureAvailable(); return entries.GetValueOrDefault(operationId)?.Operation; }
    }

    public IReadOnlyList<WebsitePublicationOperationDto> History(Guid applicationId, int maximum)
    {
        lock (gate)
        {
            EnsureAvailable();
            return entries.Values.Where(entry => entry.Operation.ApplicationId == applicationId)
                .OrderByDescending(entry => entry.Operation.CreatedAt).Take(Math.Clamp(maximum, 1, 100)).Select(entry => entry.Operation).ToArray();
        }
    }

    public WebsitePublicationOperationDto Update(Guid operationId, Func<WebsitePublicationOperationDto, WebsitePublicationOperationDto> update)
    {
        lock (gate)
        {
            EnsureAvailable();
            var before = entries.GetValueOrDefault(operationId) ?? throw new WebsitePublicationException("website.operation_not_found", 404);
            var after = update(before.Operation);
            var updated = before with { Operation = after };
            if (!Valid(updated)) throw new WebsitePublicationException("website.operation_store_unavailable", 503);
            entries[operationId] = updated;
            Commit();
            return after;
        }
    }

    public IReadOnlyList<WebsitePublicationOperationDto> InterruptActive()
    {
        lock (gate)
        {
            EnsureAvailable();
            var interrupted = entries.Values.Where(entry => Active(entry.Operation)).Select(entry => entry.Operation).ToArray();
            if (interrupted.Length == 0) return [];
            var now = DateTimeOffset.UtcNow;
            foreach (var operation in interrupted)
            {
                entries[operation.OperationId] = entries[operation.OperationId] with { Operation = operation with
                {
                    State = WebsitePublicationState.Interrupted, Stage = WebsitePublicationStage.Interrupted,
                    ProblemCode = "website.operation_interrupted", RecoveryProblemCode = "website.recheck_required", CompletedAt = now,
                } };
            }
            Commit();
            return interrupted;
        }
    }

    private void Commit()
    {
        try
        {
            var retained = entries.Values.OrderByDescending(entry => entry.Operation.CreatedAt)
                .Where(entry => Active(entry.Operation)).Concat(entries.Values.Where(entry => !Active(entry.Operation)).OrderByDescending(entry => entry.Operation.CreatedAt)
                    .Take(Math.Max(0, RetainedOperations - entries.Values.Count(another => Active(another.Operation)))))
                .DistinctBy(entry => entry.Operation.OperationId).OrderBy(entry => entry.Operation.CreatedAt).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(retained, Json));
            File.Move(temporary, path, true);
            entries.Clear();
            foreach (var entry in retained) entries.Add(entry.Operation.OperationId, entry);
        }
        catch
        {
            unavailable = true;
            throw new WebsitePublicationException("website.operation_store_unavailable", 503);
        }
    }

    private void EnsureAvailable()
    {
        if (unavailable) throw new WebsitePublicationException("website.operation_store_unavailable", 503);
    }

    private static string RequestIdentity(PublishWebsiteRequest request) => string.Join('|', request.ApplicationId.ToString("D"), request.WebServerId,
        request.Domain, request.CertificateId?.ToString("D") ?? "-", request.ContactEmail ?? "-", request.AcceptedTerms, request.PublicReachabilityConfirmed, request.Confirmed);
    private static string Reference(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool Valid(Entry entry) => entry.Operation is { } operation
        && operation.OperationId != Guid.Empty && operation.ApplicationId != Guid.Empty
        && !string.IsNullOrWhiteSpace(operation.WebServerId) && operation.WebServerId.Length <= 128
        && !string.IsNullOrWhiteSpace(operation.Domain) && operation.Domain.Length <= 253
        && operation.SiteId is null or { Length: > 0 and <= 128 }
        && Enum.IsDefined(operation.State) && Enum.IsDefined(operation.Stage) && operation.CreatedAt != default
        && operation.Checks is not null && operation.Checks.Count <= 8
        && operation.Checks.All(check => !string.IsNullOrWhiteSpace(check.Name) && check.Name.Length <= 32 && check.Observer.Length <= 32 && Enum.IsDefined(check.State))
        && entry.ActorReference.Length == 64 && entry.IdempotencyReference.Length == 64 && entry.RequestReference.Length == 64;
}
