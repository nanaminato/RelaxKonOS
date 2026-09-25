using System.Net;
using RelaxKonOS.Protocol.Identity;

namespace RelaxKonOS.Client.Services.Auth;

/// <summary>
/// Resolves the address entered on the sign-in screen without ever sending credentials.
/// Bare host names are tried as HTTPS first and fall back to HTTP only when HTTPS is unavailable.
/// </summary>
public sealed class ServerEndpointResolver(HttpClient http)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    public async Task<ServerEndpointResolution> ResolveAsync(string value, CancellationToken ct = default)
    {
        var candidates = CreateCandidates(value);
        if (candidates.Count == 0)
            return ServerEndpointResolution.Invalid;

        foreach (var candidate in candidates)
        {
            if (await IsLoginEndpointAvailableAsync(candidate, ct))
                return new ServerEndpointResolution(candidate, IsValidInput: true);
        }

        return new ServerEndpointResolution(null, IsValidInput: true);
    }

    internal static IReadOnlyList<string> CreateCandidates(string value)
    {
        var input = value.Trim();
        if (string.IsNullOrEmpty(input)) return Array.Empty<string>();

        var hasExplicitScheme = input.Contains("://", StringComparison.Ordinal);
        if (hasExplicitScheme)
            return TryNormalize(input, out var endpoint) ? [endpoint] : Array.Empty<string>();

        // An unqualified address is the only form for which the client chooses a transport.  This
        // deliberately leaves an explicitly supplied http:// address alone: it is often the local
        // development endpoint, and silently upgrading or downgrading it would override user intent.
        return new[] { "https://" + input, "http://" + input }
            .Select(candidate => TryNormalize(candidate, out var endpoint) ? endpoint : null)
            .Where(endpoint => endpoint is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<bool> IsLoginEndpointAvailableAsync(string endpoint, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Options,
            new Uri(new Uri(endpoint + "/", UriKind.Absolute), AuthApiRoutes.Login.TrimStart('/')));

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            // ASP.NET Core reports 405 for OPTIONS on the POST-only login route.  It is the expected
            // positive probe result; 404 means this is not the RelaxKonOS authentication endpoint.
            return response.StatusCode != HttpStatusCode.NotFound
                   && (int)response.StatusCode is >= 200 and < 500;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static bool TryNormalize(string value, out string endpoint)
    {
        endpoint = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath is not "/" and not "")
        {
            return false;
        }

        endpoint = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).TrimEnd('/');
        return true;
    }
}

/// <summary>The resolved absolute server address, or a validation/probe failure.</summary>
public sealed record ServerEndpointResolution(string? Endpoint, bool IsValidInput)
{
    public static ServerEndpointResolution Invalid { get; } = new(null, IsValidInput: false);
    public bool IsResolved => Endpoint is not null;
}
