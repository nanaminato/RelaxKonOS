using System.Net;
using System.Text.Json;

namespace RelaxKonOS.ServerModule.CloudStorage;

/// <summary>Baidu's device_token flow. Device codes and credentials never belong in public DTOs.</summary>
internal sealed class BaiduDeviceAuthorization(Func<Uri, bool, HttpClient> transport)
{
    private const string OAuthOrigin = "https://openapi.baidu.com";
    private const int MaximumResponseBytes = 64 * 1024;

    internal sealed record DeviceGrant(string DeviceCode, string UserCode, Uri VerificationUri,
        Uri? QrCodeUri, int ExpiresIn, int Interval)
    {
        public override string ToString() => "Baidu device grant [redacted]";
    }

    internal enum PollStatus { Pending, SlowDown, Denied, Expired, Failed, Authorized }
    internal sealed record TokenGrant(string AccessToken, string RefreshToken, int ExpiresIn)
    {
        public override string ToString() => "Baidu token grant [redacted]";
    }
    internal sealed record PollResult(PollStatus Status, TokenGrant? Tokens = null);

    internal async Task<DeviceGrant> StartAsync(string clientId, CancellationToken cancellationToken)
    {
        ValidateCredential(clientId);
        using var response = await GetAsync("/oauth/2.0/device/code", new()
        {
            ["response_type"] = "device_code", ["client_id"] = clientId, ["scope"] = "basic,netdisk"
        }, cancellationToken);
        try
        {
            var root = response.RootElement;
            if (root.TryGetProperty("error", out _)) throw InvalidResponse();
            return new DeviceGrant(RequiredString(root, "device_code", 8192),
                RequiredString(root, "user_code", 128),
                BrowserUri(RequiredString(root, "verification_url", 4096)),
                root.TryGetProperty("qrcode_url", out var qr) && qr.ValueKind != JsonValueKind.Null
                    ? BrowserUri(RequiredString(root, "qrcode_url", 4096)) : null,
                PositiveInt(root, "expires_in", 86400), PositiveInt(root, "interval", 3600));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        { throw InvalidResponse(); }
    }

    internal async Task<PollResult> PollAsync(string clientId, string clientSecret, string deviceCode,
        CancellationToken cancellationToken)
    {
        ValidateCredential(clientId);
        ValidateCredential(clientSecret);
        ValidateCredential(deviceCode);
        // Baidu's official SDK specifies GET, grant_type=device_token and parameter "code".
        // Never log the URI or return upstream exceptions: the query contains the client secret.
        using var response = await GetAsync("/oauth/2.0/token", new()
        {
            ["grant_type"] = "device_token", ["code"] = deviceCode,
            ["client_id"] = clientId, ["client_secret"] = clientSecret
        }, cancellationToken);
        try
        {
            var root = response.RootElement;
            if (root.TryGetProperty("error", out var error))
                return new PollResult(error.ValueKind == JsonValueKind.String ? error.GetString() switch
                {
                    "authorization_pending" => PollStatus.Pending,
                    "slow_down" => PollStatus.SlowDown,
                    "authorization_declined" or "access_denied" => PollStatus.Denied,
                    "expired_token" => PollStatus.Expired,
                    _ => PollStatus.Failed
                } : PollStatus.Failed);
            return new PollResult(PollStatus.Authorized, new TokenGrant(
                RequiredString(root, "access_token", 8192), RequiredString(root, "refresh_token", 8192),
                PositiveInt(root, "expires_in", 31536000)));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        { throw InvalidResponse(); }
    }

    private async Task<JsonDocument> GetAsync(string path, Dictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var uri = new Uri(OAuthOrigin + path + "?" + string.Join("&", fields.Select(field =>
                Uri.EscapeDataString(field.Key) + "=" + Uri.EscapeDataString(field.Value))));
            using var client = transport(uri, false);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            // OAuth errors can use 400. Redirects and non-OAuth errors must not be followed or echoed.
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest)
                throw InvalidResponse();
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw InvalidResponse();
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            while (true)
            {
                var read = await source.ReadAsync(bytes, timeout.Token);
                if (read == 0) break;
                if (buffer.Length + read > MaximumResponseBytes) throw InvalidResponse();
                buffer.Write(bytes, 0, read);
            }
            return JsonDocument.Parse(buffer.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException
            or InvalidOperationException or OperationCanceledException or ArgumentException)
        { throw InvalidResponse(); }
    }

    private static Uri BrowserUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Host != "openapi.baidu.com" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !uri.AbsolutePath.StartsWith("/device", StringComparison.Ordinal)
            || (uri.AbsolutePath.Length > 7 && uri.AbsolutePath[7] != '/'))
            throw InvalidResponse();
        // Older Baidu responses advertise HTTP. Only upgrade this exact trusted official origin.
        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
    }

    private static string RequiredString(JsonElement root, string name, int maxLength)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { Length: > 0 } text || text.Length > maxLength
            || text.Any(char.IsControl) || string.IsNullOrWhiteSpace(text)) throw InvalidResponse();
        return text;
    }

    private static int PositiveInt(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out var value) || !value.TryGetInt32(out var number)
            || number <= 0 || number > maximum) throw InvalidResponse();
        return number;
    }

    private static void ValidateCredential(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsControl))
            throw new ArgumentException("Invalid Baidu authorization credential.");
    }
    private static InvalidOperationException InvalidResponse() => new("Baidu authorization request failed.");
}
