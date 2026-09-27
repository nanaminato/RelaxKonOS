using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.Auth;

/// <summary>A user-selected reachable address for pairing other devices; it is not the local transport address.</summary>
public interface IOwnerDevicePairingEndpointStore
{
    Task<string?> GetAsync(string serviceId, CancellationToken ct = default);
    Task SaveAsync(string serviceId, string endpoint, CancellationToken ct = default);
}

public sealed class OwnerDevicePairingEndpointStore : IOwnerDevicePairingEndpointStore
{
    private readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RelaxKonOS", "owner-device-pairing-endpoints.json");

    public async Task<string?> GetAsync(string serviceId, CancellationToken ct = default)
        => (await ReadAsync(ct)).Endpoints.SingleOrDefault(item => item.ServiceId == serviceId)?.Endpoint;

    public async Task SaveAsync(string serviceId, string endpoint, CancellationToken ct = default)
    {
        var normalized = OwnerDevicePairingEndpointRules.Normalize(endpoint);
        var endpoints = (await ReadAsync(ct)).Endpoints
            .Where(item => item.ServiceId != serviceId)
            .Append(new OwnerDevicePairingEndpoint(serviceId, normalized))
            .ToArray();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, new OwnerDevicePairingEndpointCollection(endpoints), RelaxKonOSJsonOptions.Default, ct);
        File.Move(temporary, path, overwrite: true);
    }

    private async Task<OwnerDevicePairingEndpointCollection> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(path)) return new OwnerDevicePairingEndpointCollection(Array.Empty<OwnerDevicePairingEndpoint>());
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<OwnerDevicePairingEndpointCollection>(stream, RelaxKonOSJsonOptions.Default, ct)
            ?? new OwnerDevicePairingEndpointCollection(Array.Empty<OwnerDevicePairingEndpoint>());
    }
}

public static class OwnerDevicePairingEndpointRules
{
    public static string Normalize(string endpoint)
    {
        var normalized = ServerConnectionIdentityRules.NormalizeServerUrl(endpoint);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || uri.IsLoopback)
            throw new ArgumentException("The pairing address must be a non-loopback HTTP(S) URL.", nameof(endpoint));
        return normalized;
    }

    public static bool UsesInsecureHttp(string endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == "http";
}

public sealed record OwnerDevicePairingEndpoint(string ServiceId, string Endpoint);
public sealed record OwnerDevicePairingEndpointCollection(IReadOnlyList<OwnerDevicePairingEndpoint> Endpoints);
