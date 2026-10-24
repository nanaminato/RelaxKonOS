using System.Globalization;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes;

/// <summary>
/// Closed policy for the host download proxy: what may be used, which targets bypass it, and when a
/// failed attempt may be repeated. The Helper re-validates whatever the Server resolved, so a corrupted
/// or hand-built request can never widen the allowed route.
/// </summary>
public static class RuntimeDownloadPolicy
{
    public const int MaximumEndpointLength = 512;
    public const int MaximumBypassEntries = 32;
    /// <summary>One initial attempt plus two retries. Downloads are bounded by the transfer deadline too.</summary>
    public const int MaximumAttempts = 3;
    public static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(10);

    /// <summary>Builds a validated route from the saved preference. Throws instead of guessing.</summary>
    public static RuntimeDownloadProxy Create(string? endpoint, string? bypassText)
    {
        var bypass = new List<string>();
        foreach (var entry in (bypassText ?? string.Empty).Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (bypass.Count == MaximumBypassEntries) throw Invalid();
            bypass.Add(entry.ToLowerInvariant());
        }
        var proxy = new RuntimeDownloadProxy(endpoint?.Trim() ?? string.Empty, bypass.ToArray());
        Validate(proxy);
        return proxy;
    }

    public static void Validate(RuntimeDownloadProxy proxy)
    {
        if (proxy?.Endpoint is not { Length: > 0 and <= MaximumEndpointLength } endpoint) throw Invalid();
        if (endpoint.Any(char.IsControl) || endpoint.Any(char.IsWhiteSpace) || endpoint.Contains('\\')) throw Invalid();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) throw Invalid();
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host)) throw Invalid();
        if (uri.Port is < 1 or > 65535 || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw Invalid();
        if (uri.AbsolutePath is not ("" or "/")) throw Invalid();
        // A proxy URL may embed credentials, so only the length is bounded here and the value is never echoed back.
        if (uri.UserInfo.Length > 256 || uri.UserInfo.Any(char.IsControl)) throw Invalid();
        if (proxy.Bypass is null || proxy.Bypass.Count > MaximumBypassEntries) throw Invalid();
        foreach (var entry in proxy.Bypass) ValidateBypass(entry);
    }

    /// <summary>True when this target must not be tunnelled through the configured proxy.</summary>
    public static bool Bypasses(RuntimeDownloadProxy proxy, Uri target)
    {
        if (proxy.Bypass is null) return false;
        var host = target.Host.TrimEnd('.').ToLowerInvariant();
        foreach (var entry in proxy.Bypass)
        {
            var (name, port) = Split(entry);
            if (port is { } expected && expected != target.Port) continue;
            if (host.Equals(name, StringComparison.Ordinal) || host.EndsWith("." + name, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Only transport-level failures are repeated; policy refusals are final on the first attempt.</summary>
    public static bool IsRetryable(string? problemCode) => problemCode is "runtime.network_unavailable"
        or "runtime.source_timeout" or "runtime.source_rate_limited" or "runtime.source_unavailable";

    /// <summary>Deterministic backoff, capped so a hostile Retry-After cannot stall an operation.</summary>
    public static TimeSpan Delay(int attempt, DateTimeOffset now, DateTimeOffset? retryAfter)
    {
        var delay = attempt <= 1 ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromMilliseconds(1500);
        if (retryAfter is { } value) delay = value - now;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        return delay > MaximumRetryDelay ? MaximumRetryDelay : delay;
    }

    /// <summary>Names the route without its credentials, for diagnostics and audit text.</summary>
    public static string Describe(RuntimeDownloadProxy proxy)
    {
        if (!Uri.TryCreate(proxy.Endpoint, UriKind.Absolute, out var uri)) return "invalid";
        return uri.Scheme + "://" + uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The endpoint as the network stack needs it, with userinfo removed from the address itself.</summary>
    public static (Uri Address, string? User, string? Password) Decompose(RuntimeDownloadProxy proxy)
    {
        Validate(proxy);
        var uri = new Uri(proxy.Endpoint, UriKind.Absolute);
        var address = new Uri(uri.Scheme + "://" + uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture) + "/");
        if (uri.UserInfo.Length == 0) return (address, null, null);
        var separator = uri.UserInfo.IndexOf(':');
        var user = Uri.UnescapeDataString(separator < 0 ? uri.UserInfo : uri.UserInfo[..separator]);
        var password = separator < 0 ? null : Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]);
        return (address, user, password);
    }

    private static (string Name, int? Port) Split(string entry)
    {
        var separator = entry.LastIndexOf(':');
        if (separator <= 0 || entry.IndexOf(':') != separator) return (entry.TrimStart('.'), null);
        return (entry[..separator].TrimStart('.'),
            int.TryParse(entry[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                && port is >= 1 and <= 65535 ? port : null);
    }

    private static void ValidateBypass(string? entry)
    {
        if (entry is not { Length: > 0 and <= 253 }) throw Invalid();
        var (name, port) = Split(entry);
        if (port is null && entry.Contains(':')) throw Invalid();
        if (name.Length == 0 || name.Split('.').Any(label => label.Length is 0 or > 63
            || !label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || label[0] == '-' || label[^1] == '-')) throw Invalid();
    }

    private static RuntimePolicyException Invalid() => new("runtime.download_proxy_invalid");
}
