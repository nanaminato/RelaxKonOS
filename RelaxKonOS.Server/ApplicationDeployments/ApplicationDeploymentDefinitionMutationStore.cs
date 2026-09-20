using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>
/// Durable idempotency ledger for immediate definition mutations. Deployment operations have their
/// own operation ledger; create/update need the same retry guarantee without pretending to be a
/// background operation.
/// </summary>
internal sealed class ApplicationDeploymentDefinitionMutationStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path;
    private Ledger ledger = new([]);
    private bool unavailable;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public ApplicationDeploymentDefinitionMutationStore(IHostEnvironment environment, ApplicationDeploymentOptions options)
    {
        path = Path.Combine(environment.ContentRootPath, options.RootDirectory, "definition-mutations.json");
        try
        {
            if (!File.Exists(path)) return;
            ledger = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(path), Json) ?? throw new JsonException();
            if (ledger.Entries is null || ledger.Entries.Select(entry => entry.KeyReference).Distinct(StringComparer.Ordinal).Count() != ledger.Entries.Length
                || ledger.Entries.Any(entry => !Valid(entry))) throw new JsonException();
        }
        catch { unavailable = true; }
    }

    public async Task<T> ExecuteAsync<T>(string actor, string key, string kind, string requestReference, Func<Task<T>> action)
    {
        ValidateKey(key);
        await gate.WaitAsync();
        try
        {
            EnsureAvailable();
            var actorReference = ApplicationDeploymentValidation.Reference(actor);
            var keyReference = ApplicationDeploymentValidation.Reference(actorReference + "\n" + key);
            var existing = ledger.Entries.FirstOrDefault(entry => entry.KeyReference == keyReference);
            if (existing is not null)
            {
                if (existing.ActorReference != actorReference || existing.Kind != kind || existing.RequestReference != requestReference)
                    throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.IdempotencyConflict);
                return JsonSerializer.Deserialize<T>(existing.Response, Json)
                    ?? throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.StoreUnavailable, 503);
            }

            var response = await action();
            var entry = new Entry(actorReference, keyReference, kind, requestReference, JsonSerializer.Serialize(response, Json), DateTimeOffset.UtcNow);
            Commit(new([.. ledger.Entries.Append(entry).OrderByDescending(item => item.CompletedAt).Take(500)]));
            return response;
        }
        finally { gate.Release(); }
    }

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
        catch
        {
            unavailable = true;
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.StoreUnavailable, 503);
        }
    }

    private void EnsureAvailable()
    {
        if (unavailable) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.StoreUnavailable, 503);
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(character => character < 33 || character > 126))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.IdempotencyRequired, 400);
    }

    private static bool Valid(Entry entry) => ApplicationDeploymentValidation.IsValidReference(entry.ActorReference, 64)
        && ApplicationDeploymentValidation.IsValidReference(entry.KeyReference, 64)
        && ApplicationDeploymentValidation.IsValidReference(entry.RequestReference, 64)
        && entry.Kind is "create" or "update" && entry.Response is { Length: > 0 and <= 131072 } && entry.CompletedAt != default;

    private sealed record Entry(string ActorReference, string KeyReference, string Kind, string RequestReference, string Response, DateTimeOffset CompletedAt);
    private sealed record Ledger(Entry[] Entries);
}
