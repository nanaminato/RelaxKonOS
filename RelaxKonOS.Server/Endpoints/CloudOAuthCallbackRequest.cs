using Microsoft.AspNetCore.Http;
using RelaxKonOS.ModuleHost.Protocol;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>A narrow, anonymous ingress, not an anonymous ModuleHost proxy.</summary>
internal static class CloudOAuthCallbackRequest
{
    internal const string PublicPath = "/api/cloud-storage/oauth/google/callback";
    internal const string ModulePath = "cloud/authorization/google/callback";

    internal static bool TryRead(HttpRequest request, out QueryString query)
    {
        query = QueryString.Empty;
        if (!HttpMethods.IsGet(request.Method) || request.QueryString.Value?.Length > 16384
            || request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding")) return false;
        var values = request.Query;
        // Google may append scope, authuser and prompt. They are deliberately not relayed.
        if (!One(values, "state", 128, out var state) || state.Length < 32
            || !state.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        var hasCode = values.ContainsKey("code");
        var hasError = values.ContainsKey("error");
        if (hasCode == hasError) return false;
        var key = hasCode ? "code" : "error";
        if (!One(values, key, hasCode ? 8192 : 128, out var value)) return false;
        if (hasError && !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return false;
        query = QueryString.Create(new Dictionary<string, string?> { ["state"] = state, [key] = value });
        return true;
    }

    private static bool One(IQueryCollection values, string name, int max, out string value)
    {
        value = "";
        if (!values.TryGetValue(name, out var entries) || entries.Count != 1
            || entries[0] is not { Length: > 0 } text || text.Length > max
            || text.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return false;
        value = text;
        return true;
    }

    internal static HttpRequestMessage Create(Uri moduleEndpoint, string serviceToken, QueryString query)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(moduleEndpoint, ModulePath + query));
        request.Headers.TryAddWithoutValidation(ModuleHostProtocol.ServiceTokenHeader, serviceToken);
        // Never forward cookies, Authorization, module-user/role or request-context headers.
        return request;
    }

    internal static void Harden(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers["Pragma"] = "no-cache";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
    }
}
