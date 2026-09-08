using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RelaxKonOS.Protocol.ImageMirrors;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>Authenticated CRUD for user-owned registry mirror prefixes.</summary>
public static class ImageMirrorEndpoints
{
    private static readonly Guid DefaultMirrorId = Guid.Empty;

    public static IEndpointRouteBuilder MapImageMirrorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"/{RelaxKonOS.Protocol.Common.RemoteOsEndpoints.ApiVersionPrefix}/image-mirrors").RequireAuthorization().WithTags("Image mirrors");
        group.MapGet("/{target}", (string target, ClaimsPrincipal principal, IImageMirrorRepository mirrors) =>
        {
            if (!TryGetOwnerAndTarget(target, principal, out var userId, out var parsedTarget)) return Results.BadRequest();
            var saved = mirrors.List(userId, parsedTarget);
            var result = new List<ImageMirrorDto>
            {
                new(DefaultMirrorId, parsedTarget, "Default", string.Empty, !saved.Any(x => x.IsSelected)),
            };
            result.AddRange(saved.Select(ToDto));
            return Results.Ok(result);
        });
        group.MapPost("/{target}", (string target, CreateImageMirrorRequest request, ClaimsPrincipal principal, IImageMirrorRepository mirrors) =>
        {
            if (!TryGetOwnerAndTarget(target, principal, out var userId, out var parsedTarget)
                || !TryValidate(request.Name, request.Endpoint, out var name, out var endpoint))
                return Results.BadRequest(new { message = "A name and HTTPS registry host are required." });
            var saved = mirrors.Create(new ImageMirror { UserId = userId, Target = parsedTarget, Name = name, Endpoint = endpoint });
            return Results.Created($"/{RelaxKonOS.Protocol.Common.RemoteOsEndpoints.ApiVersionPrefix}/image-mirrors/{target}/{saved.Id}", ToDto(saved));
        });
        group.MapPut("/{target}/{id:guid}", (string target, Guid id, UpdateImageMirrorRequest request, ClaimsPrincipal principal, IImageMirrorRepository mirrors) =>
        {
            if (!TryGetOwnerAndTarget(target, principal, out var userId, out var parsedTarget)
                || !TryValidate(request.Name, request.Endpoint, out var name, out var endpoint))
                return Results.BadRequest(new { message = "A name and HTTPS registry host are required." });
            var saved = mirrors.Update(new ImageMirror { Id = id, UserId = userId, Target = parsedTarget, Name = name, Endpoint = endpoint });
            return saved is null ? Results.NotFound() : Results.Ok(ToDto(saved));
        });
        group.MapDelete("/{target}/{id:guid}", (string target, Guid id, ClaimsPrincipal principal, IImageMirrorRepository mirrors) =>
        {
            if (!TryGetOwnerAndTarget(target, principal, out var userId, out var parsedTarget)) return Results.BadRequest();
            return mirrors.Delete(userId, parsedTarget, id) ? Results.NoContent() : Results.NotFound();
        });
        group.MapPut("/{target}/selection", (string target, SelectImageMirrorRequest request, ClaimsPrincipal principal, IImageMirrorRepository mirrors) =>
        {
            if (!TryGetOwnerAndTarget(target, principal, out var userId, out var parsedTarget)) return Results.BadRequest();
            var mirrorId = request.MirrorId == DefaultMirrorId ? null : request.MirrorId;
            return mirrors.Select(userId, parsedTarget, mirrorId) ? Results.NoContent() : Results.NotFound();
        });
        return app;
    }

    private static bool TryGetOwnerAndTarget(string target, ClaimsPrincipal principal, out Guid userId, out ImageMirrorTarget parsedTarget)
    {
        userId = Guid.Empty;
        parsedTarget = default;
        var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(subject, out userId)
            && Enum.TryParse(target, ignoreCase: true, out parsedTarget) && Enum.IsDefined(parsedTarget);
    }

    private static bool TryValidate(string? rawName, string? rawEndpoint, out string name, out string endpoint)
    {
        name = rawName?.Trim() ?? string.Empty;
        endpoint = string.Empty;
        if (name.Length is < 1 or > 80 || string.IsNullOrWhiteSpace(rawEndpoint)) return false;
        var candidate = rawEndpoint.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal)) candidate = $"https://{candidate}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath is not "/" || candidate.Length > 255)
            return false;
        endpoint = uri.Authority.ToLowerInvariant();
        return true;
    }

    private static ImageMirrorDto ToDto(ImageMirror mirror) => new(mirror.Id, mirror.Target, mirror.Name, mirror.Endpoint, mirror.IsSelected);
}
