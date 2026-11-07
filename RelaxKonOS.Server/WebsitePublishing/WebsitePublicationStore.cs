using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.WebServers;
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
    internal const string SiteApplicationUnverified = "website.site_application_unverified";
    internal const string AssociationRecoveryFailed = "website.application_association_recovery_failed";
    private readonly object gate = new();
    private readonly string path;
    private readonly Dictionary<Guid, Entry> entries = [];
    private bool unavailable;
    private static readonly JsonSerializerOptions Json = new(RelaxKonOSJsonOptions.Default)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal sealed record BootstrapBinding(WebServerSiteDto Site, string ConfigurationSha256, string CertificateFingerprint);
    internal sealed record Entry(WebsitePublicationOperationDto Operation, string ActorReference, string IdempotencyReference,
        string RequestReference, BootstrapBinding? Binding = null);

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

    public WebsitePublicationOperationDto Start(PublishWebsiteRequest request, string actor, string idempotencyKey, Action validateNew, out bool created)
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
            if (entries.Values.Any(entry => HasUnverifiedSite(entry.Operation) && ConflictsWith(entry.Operation, request)))
                throw new WebsitePublicationException("website.recheck_required");
            if (entries.Values.Any(entry => Active(entry.Operation) && ConflictsWith(entry.Operation, request)))
                throw new WebsitePublicationException("website.operation_conflict");

            validateNew();
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

    internal BootstrapBinding? GetBootstrapBinding(Guid operationId)
    {
        lock (gate) { EnsureAvailable(); return entries.GetValueOrDefault(operationId)?.Binding; }
    }

    internal Guid RecordBootstrapBinding(Guid operationId, BootstrapBinding binding)
    {
        lock (gate)
        {
            EnsureAvailable();
            var before = entries.GetValueOrDefault(operationId) ?? throw new WebsitePublicationException("website.operation_not_found", 404);
            if (before.Binding is not null || before.Operation.BootstrapResumeStartedAt is not null)
                throw new WebsitePublicationException("website.recheck_required");
            var id = Guid.NewGuid();
            var updated = before with
            {
                Operation = before.Operation with { BootstrapBindingOperationId = id, BootstrapResumeStartedAt = DateTimeOffset.UtcNow },
                Binding = binding,
            };
            if (!Valid(updated)) throw new WebsitePublicationException("website.operation_store_unavailable", 503);
            entries[operationId] = updated;
            Commit();
            return id;
        }
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

    /// <summary>
    /// Read-only view of publications with ongoing work or unconfirmed compensation.
    /// Only the coordinator can reconcile active operations.
    /// </summary>
    public IReadOnlyList<WebsitePublicationOperationDto> Pending()
    {
        lock (gate)
        {
            EnsureAvailable();
            return entries.Values.Select(entry => entry.Operation).Where(RequiresOwner).ToArray();
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
                    InterruptedFromStage = operation.Stage,
                    ProblemCode = "website.operation_interrupted",
                    RecoveryProblemCode = operation.Stage == WebsitePublicationStage.Configuring || HasUnverifiedSite(operation)
                        ? SiteApplicationUnverified : "website.recheck_required",
                    CompletedAt = now,
                } };
            }
            Commit();
            return interrupted;
        }
    }

    private static bool RequiresOwner(WebsitePublicationOperationDto operation) =>
        Active(operation) || HasUnverifiedSite(operation);

    private static bool ConflictsWith(WebsitePublicationOperationDto operation, PublishWebsiteRequest request) =>
        operation.ApplicationId == request.ApplicationId
        || (operation.WebServerId == request.WebServerId && operation.Domain == request.Domain);

    private static bool HasUnverifiedSite(WebsitePublicationOperationDto operation) =>
        operation.ProblemCode == AssociationRecoveryFailed || operation.RecoveryProblemCode == SiteApplicationUnverified;

    private void Commit()
    {
        try
        {
            var obligations = entries.Values.Where(entry => RequiresOwner(entry.Operation)).ToArray();
            var history = entries.Values.Where(entry => !RequiresOwner(entry.Operation))
                .OrderByDescending(entry => entry.Operation.CreatedAt).Take(Math.Max(0, RetainedOperations - obligations.Length));
            var retained = obligations.Concat(history).OrderBy(entry => entry.Operation.CreatedAt).ToArray();
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
        && operation.BootstrapCleanupOperationId != Guid.Empty
        && (operation.BootstrapCleanedAt is null || operation.BootstrapCleanupOperationId is not null)
        && (operation.InterruptedFromStage is null || Enum.IsDefined(operation.InterruptedFromStage.Value))
        && (operation.BootstrapResumedAt is null || operation.BootstrapResumeStartedAt is not null)
        && (entry.Binding is null ? operation.BootstrapBindingOperationId is null
            : operation.BootstrapBindingOperationId is { } id && id != Guid.Empty && id != operation.OperationId
                && operation.BootstrapResumeStartedAt is not null && entry.Binding.ConfigurationSha256.Length == 64
                && !string.IsNullOrEmpty(entry.Binding.CertificateFingerprint)
                && entry.Binding.Site is { HttpsEnabled: true, AcmeHttp01: true } site
                && site.Id == operation.SiteId && site.ServerId == operation.WebServerId && site.CertificateId == operation.CertificateId
                && site.Bindings is [{ Port: 80 } binding] && binding.Domain == operation.Domain)
        && Enum.IsDefined(operation.State) && Enum.IsDefined(operation.Stage) && operation.CreatedAt != default
        && operation.Checks is not null && operation.Checks.Count <= 8
        && operation.Checks.All(check => !string.IsNullOrWhiteSpace(check.Name) && check.Name.Length <= 32 && check.Observer.Length <= 32 && Enum.IsDefined(check.State))
        && entry.ActorReference.Length == 64 && entry.IdempotencyReference.Length == 64 && entry.RequestReference.Length == 64;
}
