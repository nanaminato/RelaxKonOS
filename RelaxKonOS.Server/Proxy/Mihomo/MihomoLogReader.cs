using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Proxy.Mihomo;

internal static class MihomoLogReader
{
    internal static async Task<IReadOnlyList<ProxyLogEntryDto>> ReadAsync(IMihomoControllerClient controller,
        IProxyDiagnosticLogStore diagnostics, int limit, CancellationToken ct)
    {
        var bounded = Math.Clamp(limit, 1, 500);
        var result = await controller.GetLogsAsync(bounded, ct);
        var live = result.Succeeded ? result.Value! : [];
        var local = await diagnostics.ReadAsync(bounded, ct);
        return live.Concat(local).OrderByDescending(entry => entry.Timestamp).Take(bounded).ToArray();
    }
}
