using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RelaxKonOS.ServerModule.CloudStorage;

internal sealed class GoogleDriveAuthorization(Func<Uri, bool, HttpClient> transport)
{
    internal static string RandomValue() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static string Challenge(string verifier) => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static string Start(string clientId, string redirectUri, string state, string verifier, bool writes)
    {
        var values = new Dictionary<string, string> {
            ["client_id"] = clientId, ["redirect_uri"] = redirectUri, ["response_type"] = "code",
            ["scope"] = writes ? "https://www.googleapis.com/auth/drive" : "https://www.googleapis.com/auth/drive.readonly",
            ["access_type"] = "offline", ["prompt"] = "consent", ["state"] = state,
            ["code_challenge"] = Challenge(verifier), ["code_challenge_method"] = "S256"
        };
        return "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    }

    internal async Task<string> ExchangeAsync(string clientId, string secret, string redirectUri, string code, string? verifier, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        ct = timeout.Token;
        if (string.IsNullOrEmpty(code) || code.Length > 8192 || code.Any(char.IsControl)) throw new ArgumentException("code");
        var form = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["client_id"] = clientId,
            ["client_secret"] = secret, ["redirect_uri"] = redirectUri, ["code"] = code };
        if (verifier is not null) form["code_verifier"] = verifier;
        var endpoint = new Uri("https://oauth2.googleapis.com/token");
        using var client = transport(endpoint, false);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(form) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        // Never propagate provider response bodies, which may contain credentials.
        if (!response.IsSuccessStatusCode) throw new InvalidDataException("authorization-exchange-failed");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(bytes, ct)) > 0) {
            if (buffer.Length + count > 65536) throw new InvalidDataException("authorization-response-size");
            buffer.Write(bytes, 0, count);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        var root = document.RootElement;
        if (!root.TryGetProperty("refresh_token", out var token) || token.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("authorization-refresh-required");
        var refresh = token.GetString()!;
        if (refresh.Length is 0 or > 8192 || refresh.Any(char.IsControl)) throw new InvalidDataException("authorization-token");
        return refresh;
    }
}
