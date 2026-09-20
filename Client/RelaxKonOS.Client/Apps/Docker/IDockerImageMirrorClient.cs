using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ImageMirrors;

namespace RelaxKonOS.Client.Apps.Docker;

/// <summary>Docker Manager's client for the current user's Docker Hub-compatible mirrors.</summary>
public interface IDockerImageMirrorClient
{
    Task<IReadOnlyList<ImageMirrorDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<ImageMirrorDto> CreateAsync(CreateImageMirrorRequest request, CancellationToken cancellationToken = default);
    Task SelectAsync(Guid? mirrorId, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>JWT client for the Docker Manager's image-mirror configuration.</summary>
public sealed class DockerImageMirrorClient(HttpClient http, IAuthSession session) : IDockerImageMirrorClient
{
    public async Task<IReadOnlyList<ImageMirrorDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, ImageMirrorApiRoutes.Target.Replace("{target}", "docker"));
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<ImageMirrorDto>>(RelaxKonOSJsonOptions.Default, cancellationToken) ?? [];
    }

    public async Task<ImageMirrorDto> CreateAsync(CreateImageMirrorRequest body, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, ImageMirrorApiRoutes.Target.Replace("{target}", "docker"));
        request.Content = JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ImageMirrorDto>(RelaxKonOSJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException("The server returned an empty image mirror.");
    }

    public async Task SelectAsync(Guid? mirrorId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Put, ImageMirrorApiRoutes.Selection.Replace("{target}", "docker"));
        request.Content = JsonContent.Create(new SelectImageMirrorRequest(mirrorId), options: RelaxKonOSJsonOptions.Default);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var route = ImageMirrorApiRoutes.Mirror.Replace("{target}", "docker").Replace("{id}", Uri.EscapeDataString(id.ToString()));
        using var request = CreateRequest(HttpMethod.Delete, route);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string route)
    {
        if (session.State != AuthSessionState.Authenticated || session.ServerUrl is null || session.Tokens is null)
            throw new InvalidOperationException("Sign in before managing image mirrors.");
        var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl, UriKind.Absolute), route.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(string.IsNullOrWhiteSpace(detail)
            ? $"Image mirror request failed with HTTP {(int)response.StatusCode}." : detail, null, response.StatusCode);
    }
}
