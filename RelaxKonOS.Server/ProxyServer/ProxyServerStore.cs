using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Server.Proxy;

namespace RelaxKonOS.Server.ProxyServer;

public sealed record StoredCredential(Guid Id, string Name, Guid InboundId, string Value, bool Enabled, DateTimeOffset? ExpiresAt);
public sealed record ProxyServerReceipt(string Fingerprint, Guid OperationId);
public sealed record ProxyServerState(ProxyServerConfiguration Configuration, StoredCredential[] Credentials,
    ProxyServerRealityKey[] Keys, ProxyServerMaterial? Published, Dictionary<string, ProxyServerReceipt> Receipts)
{
    public static ProxyServerState Empty => new(new(0, []), [], [], null, new(StringComparer.Ordinal));
}

public sealed class ProxyServerStore(IProxyPlatformPaths paths, IDataProtectionProvider protection)
{
    public SemaphoreSlim Gate { get; } = new(1);
    private readonly IDataProtector _protector = protection.CreateProtector("RelaxKonOS.ProxyServer.State.v1");
    private string PathName => Path.Combine(paths.GetStateDirectory(), "proxy-server.protected");

    public async Task<ProxyServerState> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(PathName)) return ProxyServerState.Empty;
        return JsonSerializer.Deserialize<ProxyServerState>(_protector.Unprotect(await File.ReadAllTextAsync(PathName, ct)))
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
                await file.WriteAsync(System.Text.Encoding.UTF8.GetBytes(_protector.Protect(JsonSerializer.Serialize(state))), ct);
                file.Flush(true);
            }
            File.Move(temporary, PathName, true);
        }
        finally { File.Delete(temporary); }
    }
}
