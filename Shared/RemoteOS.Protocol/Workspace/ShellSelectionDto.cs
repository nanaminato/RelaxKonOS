using System.Text.Json.Serialization;

namespace RemoteOS.Protocol.Workspace;

/// <summary>
/// Cross-device intent only. Package resolution is deliberately device-local: a workspace never
/// carries a DLL path, download URL, signature or any executable material.
/// </summary>
public sealed record ShellSelectionDto
{
    [JsonPropertyName("shellId")]
    public string ShellId { get; set; } = "remoteos.default";

    [JsonPropertyName("packageId")]
    public string? PackageId { get; set; }

    [JsonPropertyName("packageVersion")]
    public string? PackageVersion { get; set; }

    public ShellSelectionDto() { }
    public ShellSelectionDto(string shellId, string? packageId = null, string? packageVersion = null) =>
        (ShellId, PackageId, PackageVersion) = (shellId, packageId, packageVersion);
}
