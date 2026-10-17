using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Server.Proxy;

namespace RelaxKonOS.Server.ProxyServer;

public sealed record StoredPrincipal(Guid Id, string DisplayName, string? RelaxKonOSUserId);

/// <summary>A principal's secret and every inbound it may use. The value never leaves the Server except as an export.</summary>
public sealed record StoredCredential(
    Guid Id, Guid PrincipalId, string Name, Guid[] InboundIds, string Value, bool Enabled, DateTimeOffset? ExpiresAt);

/// <summary>Only the hash of a subscription bearer token is persisted.</summary>
public sealed record StoredSubscription(
    Guid Id, Guid CredentialId, string TokenHash, string[] Formats, DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt, bool Revoked);

public sealed record ProxyServerReceipt(string Fingerprint, Guid OperationId, DateTimeOffset IssuedAt);

public sealed record ProxyServerState(
    ProxyServerConfiguration Configuration,
    StoredPrincipal[] Principals,
    StoredCredential[] Credentials,
    ProxyServerRealityKey[] Keys,
    ProxyServerMaterial? Published,
    ProxyServerMaterial? PreviousPublished,
    StoredSubscription[] Subscriptions,
    Dictionary<string, ProxyServerReceipt> Receipts)
{
    public static ProxyServerState Empty => new(new(0, []), [], [], [], null, null, [], new(StringComparer.Ordinal));
}

public sealed class ProxyServerStore(IProxyPlatformPaths paths, IDataProtectionProvider protection)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public SemaphoreSlim Gate { get; } = new(1);
    private readonly IDataProtector _protector = protection.CreateProtector("RelaxKonOS.ProxyServer.State.v1");
    private string PathName => Path.Combine(paths.GetStateDirectory(), "proxy-server.protected");

    public async Task<ProxyServerState> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(PathName)) return ProxyServerState.Empty;
        return JsonSerializer.Deserialize<ProxyServerState>(_protector.Unprotect(await File.ReadAllTextAsync(PathName, ct)), Json)
            ?? throw new InvalidDataException("proxy_server.state_invalid");
    }

    public async Task WriteAsync(ProxyServerState state, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var temporary = PathName + ".new";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temporary, options))
            {
                await file.WriteAsync(System.Text.Encoding.UTF8.GetBytes(_protector.Protect(JsonSerializer.Serialize(state, Json))), ct);
                file.Flush(true);
            }
            File.Move(temporary, PathName, true);
        }
        finally { File.Delete(temporary); }
    }
}
