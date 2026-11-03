using System.Text.Json;
using System.Net;
using System.Text.Json.Serialization;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Core-owned settings observation. No controller, engine, publication executor or host mutations.</summary>
public sealed class ProxySettingsReader(IProxyPlatformPaths paths) : IProxySettingsReader
{
    internal const int MaximumBytes = 64 * 1024;
    internal static readonly ProxySettingsDto Defaults = new(false, false, true, true, false, "warning", 7890,
        false, "127.0.0.1", ProxyTunSettingsDto.Default, ProxySystemProxyOptionsDto.Default);
    private static readonly JsonSerializerOptions Json = new()
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    internal static ProxySettingsDto Normalize(ProxySettingsDto value) => value with
    { Tun = value.Tun ?? ProxyTunSettingsDto.Default, SystemProxy = value.SystemProxy ?? ProxySystemProxyOptionsDto.Default };

    public Task<ProxySettingsDto> GetAsync(CancellationToken cancellationToken) => ReadAsync(paths, cancellationToken);

    internal static async Task<ProxySettingsDto> ReadAsync(IProxyPlatformPaths paths, CancellationToken cancellationToken)
    {
        var file = Path.Combine(paths.GetStateDirectory(), "mihomo-settings.json");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            RuntimeSourceStore.RequireNoLinks(file);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length is <= 0 or > MaximumBytes) throw Invalid();
            var bytes = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(bytes, cancellationToken);
            if (input.ReadByte() != -1) throw Invalid();
            return Decode(bytes);
        }
        catch (FileNotFoundException) { return Defaults; }
        catch (DirectoryNotFoundException) { return Defaults; }
        catch (Exception error) when (error is JsonException or RuntimePolicyException or UnauthorizedAccessException or IOException)
        { throw Invalid(); }
    }

    internal static ProxySettingsDto Decode(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            Unique(document.RootElement);
            var value = JsonSerializer.Deserialize<ProxySettingsDto>(bytes, Json);
            if (value is null || value.MixedPort is < 1 or > 65535 || value.LogLevel is not ("silent" or "error" or "warning" or "info" or "debug")
                || string.IsNullOrWhiteSpace(value.SystemProxyHost) || value.SystemProxyHost.Length > 128 || value.SystemProxyHost.Any(char.IsWhiteSpace) || value.SystemProxyHost.Any(char.IsControl)
                || !IPAddress.TryParse(value.SystemProxyHost, out _))
                throw Invalid();
            return Normalize(value);
        }
        catch (JsonException) { throw Invalid(); }
    }

    private static InvalidDataException Invalid() => new("Saved proxy settings are unavailable or invalid.");
    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                Unique(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) Unique(item);
    }
}
