using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Client.Services.Auth;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.Workspace;

namespace Client.Apps.Settings;

/// <summary>Workspace 图片壁纸客户端。每次请求使用绝对 URI，避免修改共享 HttpClient 的 BaseAddress。</summary>
public sealed class WorkspaceWallpaperClient(HttpClient http) : IWallpaperClient
{
    public async Task<WorkspacePreferencesDto> UploadAsync(string serverUrl, string accessToken, Guid workspaceId,
        Stream image, string fileName, CancellationToken ct = default)
    {
        var route = WorkspaceApiRoutes.Wallpaper.Replace("{id}", workspaceId.ToString("D"));
        using var form = new MultipartFormDataContent();
        using var content = new StreamContent(image);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", string.IsNullOrWhiteSpace(fileName) ? "wallpaper" : fileName);
        using var request = CreateRequest(HttpMethod.Post, serverUrl, accessToken, route);
        request.Content = form;
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        try
        {
            return await response.Content.ReadFromJsonAsync<WorkspacePreferencesDto>(RemoteOsJsonOptions.Default, ct)
                ?? throw new RemoteOsAuthException(EmptyResponse());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new RemoteOsAuthException(new ProblemDetails(
                "https://remoteos.app/problems/invalid-response",
                "Invalid server response",
                502,
                "The server returned invalid wallpaper preferences.",
                null));
        }
    }

    public async Task<byte[]> DownloadAsync(string serverUrl, string accessToken, Guid workspaceId, string blobId,
        CancellationToken ct = default)
    {
        var route = WorkspaceApiRoutes.WallpaperContent
            .Replace("{id}", workspaceId.ToString("D"))
            .Replace("{blobId}", Uri.EscapeDataString(blobId));
        using var request = CreateRequest(HttpMethod.Get, serverUrl, accessToken, route);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string serverUrl, string accessToken, string route)
        => new(method, new Uri(new Uri(serverUrl), route.TrimStart('/')))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", accessToken) },
        };

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var problem = JsonSerializer.Deserialize<ProblemDetails>(body, RemoteOsJsonOptions.Default);
                if (problem is not null && (!string.IsNullOrWhiteSpace(problem.Title) || !string.IsNullOrWhiteSpace(problem.Detail)))
                    throw new RemoteOsAuthException(problem);

                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(message.GetString()))
                {
                    throw new RemoteOsAuthException(new ProblemDetails(
                        "https://remoteos.app/problems/wallpaper-upload-failed",
                        "Wallpaper upload failed",
                        (int)response.StatusCode,
                        message.GetString(),
                        null));
                }
            }
            catch (JsonException)
            {
                // Non-JSON server responses fall through to the standard HTTP error below.
            }
        }

        throw new RemoteOsAuthException(new ProblemDetails("https://remoteos.app/problems/http-error",
            $"HTTP {(int)response.StatusCode}", (int)response.StatusCode, response.ReasonPhrase, null));
    }

    private static ProblemDetails EmptyResponse() => new("https://remoteos.app/problems/empty-response",
        "Empty response", 500, "The server did not return wallpaper preferences.", null);
}
