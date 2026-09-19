using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Workspace.SystemStyles;

namespace RelaxKonOS.Protocol.Workspace;

/// <summary>
/// The single source of truth for everything that decides how the RelaxKonOS desktop looks:
/// which colours (<see cref="Appearance"/>), which system style (<see cref="SystemStyleId"/>), and
/// which desktop shell layout (<see cref="Shell"/>). The three are stored separately on purpose so
/// a user can pair any layout with any style, and so an external shell package can never rewrite
/// the workspace-wide style without an explicit, reversible user action.
/// </summary>
public sealed record DesktopExperiencePreferencesDto
{
    [JsonPropertyName("appearance")]
    public AppearancePreferencesDto Appearance { get; set; } = AppearancePreferencesDto.Default;

    /// <summary>Cross-device user intent. Whether this device can resolve it is a device-local fact.</summary>
    [JsonPropertyName("systemStyleId")]
    public string SystemStyleId { get; set; } = SystemStyleIds.WindowsLike;

    [JsonPropertyName("shell")]
    public ShellSelectionDto Shell { get; set; } = new("relaxkonos.windows-like");

    public static DesktopExperiencePreferencesDto Default => new();
}
