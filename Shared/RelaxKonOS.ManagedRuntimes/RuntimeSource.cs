using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes;

public sealed class RuntimePolicyException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}

/// <summary>Host-owned policy. No authentication secrets; never bind this type to an install request.</summary>
public sealed record RuntimeSource(string SourceId, string RuntimeId, string Publisher, RuntimeSourceKind Kind, string BaseUri,
    string ManifestPath, string KeyId, string PublicKeySpki, bool Enabled, long TrustRevision,
    RuntimeChannel[] Channels, int MaximumMetadataBytes, int MaximumValiditySeconds,
    string[] DownloadPrefixes, string[] RedirectPrefixes, RuntimePrivateNetwork[] PrivateNetworks) : IRuntimeDownloadScope
{
    [JsonIgnore]
    public string ConfirmationFingerprint => Convert.ToHexString(SHA256.HashData(
        RuntimeJson.Serialize(this with { TrustRevision = 0 }))).ToLowerInvariant();

    public void Validate()
    {
        if (Kind != RuntimeSourceKind.HttpsManifest || !RuntimeValidation.Identifier(SourceId) || !RuntimeValidation.Identifier(RuntimeId)
            || string.IsNullOrWhiteSpace(Publisher) || Publisher.Length > 256
            || !RuntimeValidation.Identifier(KeyId) || TrustRevision < 0
            || Channels is not { Length: > 0 and <= 2 } || Channels.Distinct().Count() != Channels.Length
            || Channels.Any(c => !Enum.IsDefined(c))
            || MaximumMetadataBytes is < 1024 or > 4 * 1024 * 1024
            || MaximumValiditySeconds is < 60 or > 31 * 24 * 60 * 60
            || !RuntimeValidation.Https(BaseUri, out var root) || !root.AbsolutePath.EndsWith('/')
            || !RuntimeValidation.RelativePath(ManifestPath)
            || DownloadPrefixes is not { Length: > 0 and <= 32 } || RedirectPrefixes is null
            || RedirectPrefixes.Length > 32 || PrivateNetworks is null || PrivateNetworks.Length > 32
            || DownloadPrefixes.Concat(RedirectPrefixes).Any(p =>
                !RuntimeValidation.Https(p, out var uri) || !uri.AbsolutePath.EndsWith('/')))
            throw new RuntimePolicyException("runtime.source_invalid");
        foreach (var network in PrivateNetworks) RuntimeNetworkPolicy.ValidateGrant(network);
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeySpki), out var read);
            if (read != Convert.FromBase64String(PublicKeySpki).Length
                || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new RuntimePolicyException("runtime.source_key_invalid");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        { throw new RuntimePolicyException("runtime.source_key_invalid"); }
    }

    public bool AllowsDownload(Uri uri) => DownloadPrefixes.Any(p => RuntimeValidation.Within(p, uri));
    public bool AllowsRedirect(Uri uri) => AllowsDownload(uri) || RedirectPrefixes.Any(p => RuntimeValidation.Within(p, uri));
}

public static class RuntimeValidation
{
    public static bool Identifier(string? text) => text is { Length: > 0 and <= 128 }
        && text.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.');

    public static bool RelativePath(string? path) => path is { Length: > 0 and <= 512 }
        && path.Split('/').All(segment => segment is not ("" or "." or "..")
            && segment.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.'));

    public static bool Https(string? value, out Uri uri)
    {
        uri = null!;
        return Uri.TryCreate(value, UriKind.Absolute, out uri!) && uri.Scheme == "https"
            && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment) && !value!.Contains('\\')
            && !value.Contains('%') && !value.Split('/').Any(s => s is "." or "..");
    }

    public static bool Within(string prefix, Uri uri) => Https(prefix, out var root)
        && Https(uri.OriginalString, out _) && root.Authority == uri.Authority
        && uri.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal);
}

/// <summary>One strict format, shared by publishing tools and both host verifiers.</summary>
public static class RuntimeJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 32
    };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Read<T>(ReadOnlyMemory<byte> utf8)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8, new() { MaxDepth = 32 });
            RejectDuplicates(document.RootElement);
            return JsonSerializer.Deserialize<T>(utf8.Span, Options)
                ?? throw new RuntimePolicyException("runtime.metadata_invalid");
        }
        catch (JsonException) { throw new RuntimePolicyException("runtime.metadata_invalid"); }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new RuntimePolicyException("runtime.metadata_invalid");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
}
