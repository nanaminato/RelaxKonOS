using System.Text.Json;
using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.Git;
namespace RelaxKonOS.Server.Git;

public sealed record GitBuildRecoveryFact(string ModuleId, string Description, bool RecoveryRequired);

/// <summary>Stable ledger model and bounded read-only inspection; does not load the execution module.</summary>
public static class GitBuildLedger
{
    public sealed record Credential(Guid Id, Guid Owner, string Name, string ProtectedToken);
    // Null leaves CLI ownership unknown, including older receipts with no recorded checkpoint.
    public sealed record BuildEntry(Guid Owner, string KeyHash, string RequestHash, GitBuildOperationDto Operation,
        bool? DispatchStarted = null);
    public sealed record Ledger(Credential[] Credentials, BuildEntry[] Builds);
    public const int MaximumLedgerBytes = 32 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
    };
    private static readonly Regex Sha = new("\\A[a-fA-F0-9]{40}\\z", RegexOptions.Compiled);
    public static bool NeedsRecovery(BuildEntry entry) =>
        entry.Operation.State == "interrupted" && entry.DispatchStarted != false
        || entry.Operation.ProblemCode == "git-build.process-cleanup-failed";

    public static IReadOnlyList<GitBuildRecoveryFact> InspectLedger(string contentRoot, GitBuildOptions options, bool hasActiveWork = false)
    {
        try
        {
            return ReadLedger(Path.Combine(contentRoot, options.RootDirectory, "builds.json")).Builds
                .Where(entry => entry.Operation.State is "queued" or "running" || NeedsRecovery(entry))
                .Select(entry => new GitBuildRecoveryFact("relaxkonos.git",
                    $"Git build {entry.Operation.Id} remains {entry.Operation.State}; its execution and recovery ownership must be established before disabling Git.",
                    RecoveryRequired: !hasActiveWork || NeedsRecovery(entry)))
                .ToArray();
        }
        catch (Exception error)
        {
            return [new("relaxkonos.git", $"The Git build ledger cannot be verified ({error.GetType().Name}); its state is unknown.", RecoveryRequired: true)];
        }
    }

    public static Ledger ReadLedger(string file)
    {
        byte[] bytes;
        try
        {
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > MaximumLedgerBytes) throw new InvalidDataException("Git build ledger exceeds capacity.");
            bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            if (input.ReadByte() != -1) throw new InvalidDataException("Git build ledger changed while reading.");
        }
        catch (FileNotFoundException) { return new([], []); }
        catch (DirectoryNotFoundException) { return new([], []); }
        using var document = JsonDocument.Parse(bytes);
        static void UniqueProperties(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                { if (!names.Add(property.Name)) throw new JsonException("Duplicate ledger property."); UniqueProperties(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) UniqueProperties(item);
        }
        UniqueProperties(document.RootElement);
        var value = JsonSerializer.Deserialize<Ledger>(bytes, Json) ?? throw new JsonException();
        static bool Digest(string? digest) => digest is { Length: 64 } && digest.All(char.IsAsciiHexDigit);
        if (value.Credentials is null || value.Builds is null
            || value.Credentials.Any(entry => entry is null || entry.Id == Guid.Empty || entry.Owner == Guid.Empty
                || string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.ProtectedToken))
            || value.Credentials.Select(entry => entry.Id).Distinct().Count() != value.Credentials.Length
            || value.Builds.Any(entry => entry is null || entry.Owner == Guid.Empty || !Digest(entry.KeyHash) || !Digest(entry.RequestHash)
                || entry.Operation is not { } operation || operation.Id == Guid.Empty || operation.CreatedAt == default
                || operation.State is not ("queued" or "running" or "succeeded" or "failed" or "cancelled" or "interrupted")
                || entry.DispatchStarted == true && operation.State == "queued"
                || entry.DispatchStarted == false && (operation.State == "succeeded"
                    || operation.ProblemCode is "git-build.process-cleanup-failed" or "git-build.image-cleanup-unverified")
                || (operation.State is "queued" or "running") != (operation.FinishedAt is null)
                || operation.Logs is null || operation.Logs.Any(line => line is null)
                || string.IsNullOrWhiteSpace(operation.RepositoryUrl) || string.IsNullOrWhiteSpace(operation.Reference)
                || operation.CommitSha is null || !Sha.IsMatch(operation.CommitSha)
                || string.IsNullOrWhiteSpace(operation.ContextDirectory) || string.IsNullOrWhiteSpace(operation.Dockerfile))
            || value.Builds.Select(entry => entry.Operation.Id).Distinct().Count() != value.Builds.Length
            || value.Builds.Select(entry => entry.KeyHash).Distinct(StringComparer.Ordinal).Count() != value.Builds.Length)
            throw new JsonException("Invalid Git build ledger.");
        return value;
    }

}
