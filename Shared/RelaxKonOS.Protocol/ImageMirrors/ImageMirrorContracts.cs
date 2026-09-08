using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.ImageMirrors;

/// <summary>Services whose public images can be resolved through a user-selected mirror.</summary>
public enum ImageMirrorTarget
{
    Docker,
}

/// <summary>A user-owned registry mirror. <see cref="Endpoint"/> is a registry host, not a daemon configuration URL.</summary>
public sealed record ImageMirrorDto(Guid Id, ImageMirrorTarget Target, string Name, string Endpoint, bool IsSelected);

public sealed record CreateImageMirrorRequest(string Name, string Endpoint);
public sealed record UpdateImageMirrorRequest(string Name, string Endpoint);

/// <summary>A null mirror id means use the service's built-in/default resolver.</summary>
public sealed record SelectImageMirrorRequest(Guid? MirrorId);

public static class ImageMirrorApiRoutes
{
    private const string V1 = RemoteOsEndpoints.ApiVersionPrefix;
    public const string Target = $"/{V1}/image-mirrors/{{target}}";
    public const string Mirror = $"/{V1}/image-mirrors/{{target}}/{{id}}";
    public const string Selection = $"/{V1}/image-mirrors/{{target}}/selection";
}
