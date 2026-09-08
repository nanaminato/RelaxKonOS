using System.Text.Json;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Append-only, host-global audit trail. Payloads contain stable identifiers and problem codes only.</summary>
public sealed class ProxyAuditStore(IProxyPlatformPaths paths)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task RecordAsync(string actor, string action, string result, string? problemCode, CancellationToken cancellationToken)
    {
        var entry = new { actor, action, result, problemCode, occurredAt = DateTimeOffset.UtcNow };
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = paths.GetStateDirectory(); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "proxy-audit.jsonl");
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(entry) + Environment.NewLine, cancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally { _gate.Release(); }
    }
}
