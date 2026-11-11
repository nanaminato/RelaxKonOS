using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Files;

public static class CloudStorageApi
{
    public const string ModuleId = "cloud-storage";
    public const string Route = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-hosts/" + ModuleId + "/cloud";
}

public sealed record CloudConnectionRequest(string Name, string Protocol, string Endpoint, string? Region,
    string? Bucket, string? Prefix, string UserName, string Secret, string? SessionToken = null, bool AllowPrivateWebDav = false, bool EnableWrites = false);

public sealed record CloudConnectionDto(Guid Id, string Name, string Protocol, bool CanWrite);

public sealed record CloudEntryDto(string Reference, string Name, bool IsDirectory, long? Size, DateTimeOffset? ModifiedAt);

public sealed record CloudDirectoryDto(IReadOnlyList<CloudEntryDto> Entries, string? NextCursor);
