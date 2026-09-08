using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Capabilities;

/// <summary>Routes for host-issued, application-scoped file capabilities and media leases.</summary>
public static class AppCapabilityRoutes
{
    private const string V1 = RemoteOsEndpoints.ApiVersionPrefix;

    /// <summary>Issues a short-lived file API credential for a package application.</summary>
    public const string FileToken = $"/{V1}/app-capabilities/files/token";

    /// <summary>Creates a short-lived, single-file media playback lease.</summary>
    public const string MediaLeases = $"/{V1}/app-capabilities/media-leases";

    public static string MediaLease(string leaseId) => $"{MediaLeases}/{Uri.EscapeDataString(leaseId)}";
    public static string MediaStream(string leaseId) => $"/{V1}/media/{Uri.EscapeDataString(leaseId)}";
}

/// <summary>Scopes accepted by the file capability authentication policy.</summary>
public static class FileCapabilityScopes
{
    public const string List = "files.list";
    public const string Read = "files.read";
    public const string Write = "files.write";
    public const string Manage = "files.manage";
}

public sealed record IssueFileCapabilityRequest(string AppId, IReadOnlyList<string> Scopes);
public sealed record FileCapabilityTokenDto(string AccessToken, DateTimeOffset ExpiresAt);
public sealed record CreateMediaLeaseRequest(string AppId, string Path);
public sealed record MediaLeaseDto(string LeaseId, DateTimeOffset ExpiresAt);
