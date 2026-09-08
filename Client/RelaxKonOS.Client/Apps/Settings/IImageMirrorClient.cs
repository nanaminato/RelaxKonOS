using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ImageMirrors;

namespace RelaxKonOS.Client.Apps.Settings;

public interface IImageMirrorClient
{
    Task<IReadOnlyList<ImageMirrorDto>> ListAsync(ImageMirrorTarget target, CancellationToken cancellationToken = default);
    Task<ImageMirrorDto> CreateAsync(ImageMirrorTarget target, CreateImageMirrorRequest request, CancellationToken cancellationToken = default);
    Task SelectAsync(ImageMirrorTarget target, Guid? mirrorId, CancellationToken cancellationToken = default);
    Task DeleteAsync(ImageMirrorTarget target, Guid id, CancellationToken cancellationToken = default);
}

/// <summary>JWT client for server-owned image mirror configuration.</summary>
public sealed class ImageMirrorClient(HttpClient http, IAuthSession session) : IImageMirrorClient
{
    public async Task<IReadOnlyList<ImageMirrorDto>> ListAsync(ImageMirrorTarget target, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, ImageMirrorApiRoutes.Target.Replace("{target}", Target(target)), cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<ImageMirrorDto>>(RelaxKonOSJsonOptions.Default, cancellationToken) ?? [];
    }

    public async Task<ImageMirrorDto> CreateAsync(ImageMirrorTarget target, CreateImageMirrorRequest body, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, ImageMirrorApiRoutes.Target.Replace("{target}", Target(target)), cancellationToken);
        request.Content = JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ImageMirrorDto>(RelaxKonOSJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException("The server returned an empty image mirror.");
    }

    public async Task SelectAsync(ImageMirrorTarget target, Guid? mirrorId, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Put, ImageMirrorApiRoutes.Selection.Replace("{target}", Target(target)), cancellationToken);
        request.Content = JsonContent.Create(new SelectImageMirrorRequest(mirrorId), options: RelaxKonOSJsonOptions.Default);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task DeleteAsync(ImageMirrorTarget target, Guid id, CancellationToken cancellationToken = default)
    {
        var route = ImageMirrorApiRoutes.Mirror.Replace("{target}", Target(target)).Replace("{id}", Uri.EscapeDataString(id.ToString()));
        using var request = await CreateRequestAsync(HttpMethod.Delete, route, cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string route, CancellationToken cancellationToken)
    {
        if (session.State != AuthSessionState.Authenticated || session.ServerUrl is null || session.Tokens is null)
            throw new InvalidOperationException("Sign in before managing image mirrors.");
        var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl, UriKind.Absolute), route.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        return Task.FromResult(request);
    }

    private static string Target(ImageMirrorTarget target) => target.ToString().ToLowerInvariant();

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(string.IsNullOrWhiteSpace(detail)
            ? $"Image mirror request failed with HTTP {(int)response.StatusCode}." : detail, null, response.StatusCode);
    }
}
