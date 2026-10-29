using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes.ComponentPackages;

/// <summary>Official MSI discovery only. HTTPS metadata and a digest are not publisher-signature or install authorization.</summary>
public sealed class TailscaleOfficialReleases(Func<Uri, int, CancellationToken, Task<byte[]>>? read = null)
{
    public const string Root = "https://pkgs.tailscale.com/stable/";
    public const int MaximumMetadataBytes = 65536;
    public const int MaximumChecksumBytes = 128;
    public async Task<ComponentDownloadRelease> ResolveAsync(string rid, string? version, CancellationToken ct, RuntimeDownloadProxy? proxy = null)
    {
        Architecture(rid);
        if (version is not null) RequireStableVersion(version);
        if (proxy is not null) RuntimeDownloadPolicy.Validate(proxy);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var uri = new Uri(Root + "?mode=json&os=windows" + (version is null ? "" : "&v=" + version));
        var metadata = await Read(uri,MaximumMetadataBytes,proxy,deadline.Token);
        var candidate = ParseMetadata(metadata,rid,version);
        var checksum = await Read(new Uri(candidate.DownloadUrl + ".sha256"),MaximumChecksumBytes,proxy,deadline.Token);
        ct.ThrowIfCancellationRequested();
        deadline.Token.ThrowIfCancellationRequested();
        return candidate with { Sha256 = ParseChecksum(checksum) };
    }
    private async Task<byte[]> Read(Uri uri, int maximum, RuntimeDownloadProxy? proxy, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bytes = await (read is null ? ReadOfficialAsync(uri,maximum,proxy,ct) : read(uri,maximum,ct));
        ct.ThrowIfCancellationRequested();
        if (bytes.Length == 0 || bytes.Length > maximum) throw Invalid();
        return bytes;
    }
    public static ComponentDownloadRelease ParseMetadata(ReadOnlyMemory<byte> bytes, string rid, string? requestedVersion = null)
    {
        var architecture = Architecture(rid);
        if (bytes.Length is 0 or > MaximumMetadataBytes) throw Invalid();
        if (requestedVersion is not null) RequireStableVersion(requestedVersion);
        try
        {
            // This rejects duplicate properties throughout the metadata before reading the exact current MSI fields.
            var root = RuntimeJson.Read<JsonElement>(bytes);
            var version = root.GetProperty("MSIsVersion").GetString();
            RequireStableVersion(version);
            if (requestedVersion is not null && version != requestedVersion) throw Invalid();
            var expected = $"tailscale-setup-{version}-{architecture}.msi";
            if (root.GetProperty("MSIs").GetProperty(architecture).GetString() != expected) throw Invalid();
            return new(version!,rid,Root + expected,expected,"msi");
        }
        catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.metadata_invalid") { throw Invalid(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { throw Invalid(); }
    }
    public static string ParseChecksum(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 64 or > MaximumChecksumBytes) throw Invalid();
        // The official checksum endpoint contains a digest, optionally terminated by a single line ending.
        var text = Encoding.ASCII.GetString(bytes);
        if (!Regex.IsMatch(text,@"\A[0-9a-fA-F]{64}(?:\r?\n)?\z",RegexOptions.CultureInvariant)) throw Invalid();
        return text[..64].ToLowerInvariant();
    }
    private static string Architecture(string rid) => rid switch
    {
        "win-x64" => "amd64", "win-arm64" => "arm64",
        _ => throw new RuntimePolicyException(ComponentProblemCodes.PlatformUnsupported)
    };
    private static void RequireStableVersion(string? version)
    {
        if (version is null || !Regex.IsMatch(version,@"\A(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\z",RegexOptions.CultureInvariant)
            || !Version.TryParse(version,out var parsed) || parsed.Major < 1 || parsed.Minor % 2 != 0) throw Invalid();
    }
    private static RuntimePolicyException Invalid() => new("component.tailscale_metadata_invalid");
    private static async Task<byte[]> ReadOfficialAsync(Uri uri, int maximum, RuntimeDownloadProxy? proxy, CancellationToken ct)
    {
        using var output = new MemoryStream();
        await RuntimeHttpsTransfer.CopyAsync(new Scope(uri),uri,maximum,metadata:true,output,proxy,ct);
        return output.ToArray();
    }
    private sealed class Scope(Uri request) : IRuntimeDownloadScope
    {
        public string BaseUri => Root;
        public bool Enabled => true;
        public RuntimePrivateNetwork[] PrivateNetworks => [];
        public void Validate()
        {
            if (request.Query.Length == 0)
            {
                if (!RuntimeValidation.Within(Root,request)) throw Invalid();
                return;
            }
            var prefix = Root + "?mode=json&os=windows";
            if (request.OriginalString == prefix) return;
            if (!request.OriginalString.StartsWith(prefix + "&v=",StringComparison.Ordinal)) throw Invalid();
            RequireStableVersion(request.OriginalString[(prefix.Length+3)..]);
        }
        public bool AllowsDownload(Uri uri) => RuntimeValidation.Within(Root,uri);
        public bool AllowsRedirect(Uri uri) => false;
        public bool AllowsQuery(Uri uri) => request.Query.Length != 0 && uri.OriginalString == request.OriginalString;
    }
}
