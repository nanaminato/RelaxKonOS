using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Proxy.Mihomo;

public interface IMihomoControllerClient
{
    Task<ControllerResult<bool>> IsReachableAsync(CancellationToken cancellationToken);
    Task<ControllerResult<IReadOnlyList<ProxyGroupDto>>> GetGroupsAsync(CancellationToken cancellationToken);
    Task<string?> SelectGroupAsync(string groupName, string proxyName, CancellationToken cancellationToken);
    Task<ProxyRoutingModeDto> GetRoutingModeAsync(CancellationToken cancellationToken);
    Task<string?> SetRoutingModeAsync(ProxyRoutingMode mode, CancellationToken cancellationToken);
    Task<ProxyDelayDto> TestProxyDelayAsync(string proxyName, string url, int timeoutMilliseconds, CancellationToken cancellationToken);
    Task<ControllerResult<IReadOnlyList<ProxyConnectionDto>>> GetConnectionsAsync(CancellationToken cancellationToken);
    Task<ProxyTrafficDto> GetTrafficAsync(CancellationToken cancellationToken);
    Task<string?> CloseConnectionAsync(string connectionId, CancellationToken cancellationToken);
    Task<ControllerResult<IReadOnlyList<ProxyLogEntryDto>>> GetLogsAsync(int limit, CancellationToken cancellationToken);
    Task<ProxyDnsStatusDto> GetDnsStatusAsync(CancellationToken cancellationToken);
    Task<string?> ReloadAsync(CancellationToken cancellationToken);
}

public sealed record ControllerResult<T>(T? Value, string ProblemCode)
{
    public bool Succeeded => string.IsNullOrEmpty(ProblemCode);
    public static ControllerResult<T> Success(T value) => new(value, "");
    public static ControllerResult<T> Failure(string problemCode) => new(default, problemCode);
}

/// <summary>Bounded REST-only Mihomo adapter; it accepts only a loopback URI and hides all controller JSON.</summary>
public sealed class MihomoControllerClient : IMihomoControllerClient
{
    private readonly HttpClient _httpClient;
    private readonly IProxyControllerSecretStore _secrets;
    private readonly MihomoControllerOptions _options;
    private readonly ILogger<MihomoControllerClient>? _logger;

    public MihomoControllerClient(
        HttpClient httpClient,
        IProxyControllerSecretStore secrets,
        MihomoControllerOptions options,
        ILogger<MihomoControllerClient>? logger = null)
    {
        options.Validate();
        _httpClient = httpClient;
        _httpClient.BaseAddress = options.Endpoint;
        _httpClient.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        _secrets = secrets;
        _options = options;
        _logger = logger;
    }

    public async Task<ControllerResult<bool>> IsReachableAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, "version", null, cancellationToken);
        return response.Succeeded
            ? ControllerResult<bool>.Success(true)
            : ControllerResult<bool>.Failure(response.ProblemCode);
    }

    public async Task<ControllerResult<IReadOnlyList<ProxyGroupDto>>> GetGroupsAsync(CancellationToken cancellationToken)
    {
        var result = await GetJsonAsync("proxies", cancellationToken);
        if (!result.Succeeded) return ControllerResult<IReadOnlyList<ProxyGroupDto>>.Failure(result.ProblemCode);
        try
        {
            var groups = new List<ProxyGroupDto>();
            if (!result.Value!.RootElement.TryGetProperty("proxies", out var proxies) || proxies.ValueKind != JsonValueKind.Object)
                return ControllerResult<IReadOnlyList<ProxyGroupDto>>.Failure(ProxyProblemCodes.ControllerResponseInvalid);
            foreach (var property in proxies.EnumerateObject())
            {
                var value = property.Value;
                if (!value.TryGetProperty("all", out var all) || all.ValueKind != JsonValueKind.Array) continue;
                groups.Add(new ProxyGroupDto(property.Name, GetString(value, "type") ?? "unknown", GetString(value, "now"),
                    all.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Take(1_000).ToArray()));
            }
            return ControllerResult<IReadOnlyList<ProxyGroupDto>>.Success(groups);
        }
        finally { result.Value?.Dispose(); }
    }

    public async Task<string?> SelectGroupAsync(string groupName, string proxyName, CancellationToken cancellationToken)
    {
        if (!IsName(groupName) || !IsName(proxyName)) return ProxyProblemCodes.ControllerResponseInvalid;
        var path = "proxies/" + Uri.EscapeDataString(groupName);
        var body = JsonSerializer.Serialize(new { name = proxyName });
        return (await SendAsync(HttpMethod.Put, path, new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken)).ProblemCode;
    }

    public async Task<ProxyRoutingModeDto> GetRoutingModeAsync(CancellationToken cancellationToken)
    {
        var result = await GetJsonAsync("configs", cancellationToken);
        if (!result.Succeeded) return new(ProxyRoutingMode.Rule, result.ProblemCode);
        try
        {
            return ParseRoutingMode(GetString(result.Value!.RootElement, "mode")) is { } mode
                ? new(mode)
                : new(ProxyRoutingMode.Rule, ProxyProblemCodes.ControllerResponseInvalid);
        }
        finally { result.Value?.Dispose(); }
    }

    public Task<string?> SetRoutingModeAsync(ProxyRoutingMode mode, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode)) return Task.FromResult<string?>(ProxyProblemCodes.ConfigInvalid);
        var body = JsonSerializer.Serialize(new { mode = RoutingModeName(mode) });
        return SetRoutingModeCoreAsync(body, cancellationToken);
    }

    public async Task<ProxyDelayDto> TestProxyDelayAsync(string proxyName, string url, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        if (!IsName(proxyName) || !IsProbeUrl(url)) return new(proxyName, null, false, ProxyProblemCodes.ConfigInvalid);
        var timeout = Math.Clamp(timeoutMilliseconds, 1_000, 15_000);
        var path = "proxies/" + Uri.EscapeDataString(proxyName) + "/delay?url=" + Uri.EscapeDataString(url) + "&timeout=" + timeout;
        var result = await GetJsonAsync(path, cancellationToken);
        if (!result.Succeeded) return new(proxyName, null, false, result.ProblemCode);
        try
        {
            if (!result.Value!.RootElement.TryGetProperty("delay", out var delay) || !delay.TryGetInt32(out var milliseconds))
                return new(proxyName, null, false, ProxyProblemCodes.ControllerResponseInvalid);
            return milliseconds > 0 ? new(proxyName, milliseconds, false) : new(proxyName, null, true);
        }
        finally { result.Value?.Dispose(); }
    }

    public async Task<ControllerResult<IReadOnlyList<ProxyConnectionDto>>> GetConnectionsAsync(CancellationToken cancellationToken)
    {
        var result = await GetJsonAsync("connections", cancellationToken);
        if (!result.Succeeded) return ControllerResult<IReadOnlyList<ProxyConnectionDto>>.Failure(result.ProblemCode);
        try
        {
            if (!result.Value!.RootElement.TryGetProperty("connections", out var connections) || connections.ValueKind != JsonValueKind.Array)
                return ControllerResult<IReadOnlyList<ProxyConnectionDto>>.Failure(ProxyProblemCodes.ControllerResponseInvalid);
            var mapped = new List<ProxyConnectionDto>();
            foreach (var item in connections.EnumerateArray().Take(2_000))
            {
                var id = GetString(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                mapped.Add(new ProxyConnectionDto(id, GetString(item, "network") ?? "unknown", GetString(item, "metadata", "sourceIP") ?? "",
                    GetString(item, "metadata", "destinationIP") ?? "", GetString(item, "rule") ?? "", GetString(item, "chains") ?? "",
                    GetDateTime(item, "start") ?? DateTimeOffset.MinValue));
            }
            return ControllerResult<IReadOnlyList<ProxyConnectionDto>>.Success(mapped);
        }
        finally { result.Value?.Dispose(); }
    }

    public async Task<ProxyTrafficDto> GetTrafficAsync(CancellationToken cancellationToken)
    {
        var result = await GetJsonAsync("traffic", cancellationToken);
        if (!result.Succeeded) return new(0, 0, 0, 0, 0, result.ProblemCode);
        long uploadRate;
        long downloadRate;
        long uploadTotal;
        long downloadTotal;
        try
        {
            var traffic = result.Value!.RootElement;
            if (!TryGetInt64(traffic, "up", out uploadRate) || !TryGetInt64(traffic, "down", out downloadRate) ||
                !TryGetInt64(traffic, "upTotal", out uploadTotal) || !TryGetInt64(traffic, "downTotal", out downloadTotal))
                return new(0, 0, 0, 0, 0, ProxyProblemCodes.ControllerResponseInvalid);
        }
        finally { result.Value?.Dispose(); }

        var memoryResult = await GetJsonAsync("memory", cancellationToken);
        if (!memoryResult.Succeeded) return new(0, 0, 0, 0, 0, memoryResult.ProblemCode);
        try
        {
            return TryGetInt64(memoryResult.Value!.RootElement, "inuse", out var memory)
                ? new(uploadRate, downloadRate, uploadTotal, downloadTotal, memory)
                : new(0, 0, 0, 0, 0, ProxyProblemCodes.ControllerResponseInvalid);
        }
        finally { memoryResult.Value?.Dispose(); }
    }

    public async Task<string?> CloseConnectionAsync(string connectionId, CancellationToken cancellationToken) =>
        !IsName(connectionId) ? ProxyProblemCodes.ControllerResponseInvalid
            : (await SendAsync(HttpMethod.Delete, "connections/" + Uri.EscapeDataString(connectionId), null, cancellationToken)).ProblemCode;

    public async Task<ControllerResult<IReadOnlyList<ProxyLogEntryDto>>> GetLogsAsync(int limit, CancellationToken cancellationToken)
    {
        var bounded = Math.Clamp(limit, 1, _options.MaximumLogEntries);
        var result = await GetJsonAsync("logs?limit=" + bounded.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        if (!result.Succeeded) return ControllerResult<IReadOnlyList<ProxyLogEntryDto>>.Failure(result.ProblemCode);
        try
        {
            var logs = result.Value!.RootElement.ValueKind == JsonValueKind.Array ? result.Value.RootElement
                : result.Value.RootElement.TryGetProperty("logs", out var collection) ? collection : default;
            if (logs.ValueKind != JsonValueKind.Array) return ControllerResult<IReadOnlyList<ProxyLogEntryDto>>.Failure(ProxyProblemCodes.ControllerResponseInvalid);
            return ControllerResult<IReadOnlyList<ProxyLogEntryDto>>.Success(logs.EnumerateArray().Take(bounded).Select(item =>
                new ProxyLogEntryDto(GetDateTime(item, "time") ?? DateTimeOffset.UtcNow, GetString(item, "type") ?? "info",
                    ProxyLogSanitizer.Sanitize(GetString(item, "payload") ?? GetString(item, "message"), _options.MaximumLogMessageLength))).ToArray());
        }
        finally { result.Value?.Dispose(); }
    }

    public async Task<ProxyDnsStatusDto> GetDnsStatusAsync(CancellationToken cancellationToken)
    {
        var result = await GetJsonAsync("configs", cancellationToken);
        if (!result.Succeeded) return new(false, false, null, result.ProblemCode);
        try
        {
            var dns = result.Value!.RootElement.TryGetProperty("dns", out var value) ? value : default;
            return dns.ValueKind == JsonValueKind.Object
                ? new(GetBool(dns, "enable"), GetBool(dns, "enhanced-mode"), GetString(dns, "enhanced-mode"))
                : new(false, false, null);
        }
        finally { result.Value?.Dispose(); }
    }

    public async Task<string?> ReloadAsync(CancellationToken cancellationToken) =>
        (await SendAsync(HttpMethod.Put, "configs?force=true", new StringContent("{}", Encoding.UTF8, "application/json"), cancellationToken)).ProblemCode;

    private async Task<string?> SetRoutingModeCoreAsync(string body, CancellationToken cancellationToken) =>
        (await SendAsync(HttpMethod.Put, "configs", new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken)).ProblemCode;

    private async Task<ControllerResult<JsonDocument>> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        var result = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        if (!result.Succeeded) return ControllerResult<JsonDocument>.Failure(result.ProblemCode);
        try { return ControllerResult<JsonDocument>.Success(JsonDocument.Parse(result.Content!)); }
        catch (JsonException) { return ControllerResult<JsonDocument>.Failure(ProxyProblemCodes.ControllerResponseInvalid); }
    }

    private async Task<ControllerResponse> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _secrets.GetOrCreateAsync(cancellationToken));
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(null, response.StatusCode == HttpStatusCode.Unauthorized
                    ? ProxyProblemCodes.ControllerAuthenticationFailed
                    : ProxyProblemCodes.ControllerUnavailable);
            return new(await response.Content.ReadAsStringAsync(cancellationToken), "");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(null, ProxyProblemCodes.ControllerTimeout); }
        catch (OperationCanceledException) { throw; }
        catch (ProxyControllerSecretException) { return new(null, ProxyProblemCodes.ControllerUnavailable); }
        catch (HttpRequestException exception)
        {
            if (exception.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                _logger?.LogWarning("Mihomo controller at {Endpoint} refused the connection; Mihomo may not be running.",
                    _options.Endpoint.Authority);
            }
            else
            {
                _logger?.LogWarning("Mihomo controller at {Endpoint} is unavailable.", _options.Endpoint.Authority);
            }
            return new(null, ProxyProblemCodes.ControllerUnavailable);
        }
    }

    private static bool IsName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && value.All(character => !char.IsControl(character));
    private static bool IsProbeUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !uri.IsLoopback && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    private static ProxyRoutingMode? ParseRoutingMode(string? value) => value?.ToLowerInvariant() switch
    {
        "rule" => ProxyRoutingMode.Rule,
        "global" => ProxyRoutingMode.Global,
        "direct" => ProxyRoutingMode.Direct,
        _ => null,
    };
    private static string RoutingModeName(ProxyRoutingMode mode) => mode switch
    {
        ProxyRoutingMode.Rule => "rule",
        ProxyRoutingMode.Global => "global",
        ProxyRoutingMode.Direct => "direct",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
    private static string? GetString(JsonElement item, params string[] path)
    {
        foreach (var segment in path) if (!item.TryGetProperty(segment, out item)) return null;
        return item.ValueKind == JsonValueKind.String ? item.GetString() : item.ValueKind == JsonValueKind.Array ? string.Join(",", item.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString())) : null;
    }
    private static bool GetBool(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;
    private static bool TryGetInt64(JsonElement item, string name, out long value)
    {
        value = 0;
        return item.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value);
    }
    private static DateTimeOffset? GetDateTime(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.TryGetDateTimeOffset(out var result) ? result : null;
    private sealed record ControllerResponse(string? Content, string ProblemCode) { public bool Succeeded => string.IsNullOrEmpty(ProblemCode); }
}
