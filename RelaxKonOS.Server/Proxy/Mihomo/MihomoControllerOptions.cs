using System.Net;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Server-only controller settings. Public bindings are rejected before any request is made.</summary>
public sealed class MihomoControllerOptions
{
    public Uri Endpoint { get; init; } = new("http://127.0.0.1:9090/");
    public int TimeoutSeconds { get; init; } = 5;
    /// <summary>Maximum time to wait for a newly started managed runtime to bind its controller.</summary>
    public int StartupReadinessSeconds { get; init; } = 10;
    public int MaximumLogEntries { get; init; } = 500;
    public int MaximumLogMessageLength { get; init; } = 4_096;

    public void Validate()
    {
        if (!Endpoint.IsAbsoluteUri || Endpoint.Scheme is not ("http" or "https") || !IsLoopback(Endpoint.Host))
            throw new InvalidOperationException("Mihomo controller endpoint must be an HTTP(S) loopback address.");
        if (Endpoint.Port is <= 0 or > 65535 || TimeoutSeconds is < 1 or > 30 || StartupReadinessSeconds is < 1 or > 60
            || MaximumLogEntries is < 1 or > 500 || MaximumLogMessageLength is < 128 or > 8_192)
            throw new InvalidOperationException("Mihomo controller options are out of range.");
    }

    internal static bool IsLoopback(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
}
