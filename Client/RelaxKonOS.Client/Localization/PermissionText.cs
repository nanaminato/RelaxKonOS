using RelaxKonOS.Core.Applications;

namespace RelaxKonOS.Client.Localization;

/// <summary>Maps SDK permission identifiers to host-localized display text.</summary>
public static class PermissionText
{
    public static string DisplayName(AppPermissionDefinition permission) =>
        LocalizedText.Get($"permission.{permission.Id}.display_name", permission.DisplayName);

    public static string Description(AppPermissionDefinition permission) =>
        LocalizedText.Get($"permission.{permission.Id}.description", permission.Description);

    public static string Category(string category) =>
        LocalizedText.Get($"permission.category.{category}", ToEnglishCategory(category));

    private static string ToEnglishCategory(string category) => category switch
    {
        "server_files" => "Server files",
        "server_management" => "Server management",
        "server_network" => "Server network",
        "desktop_workspace" => "Desktop and workspace",
        "server_monitoring" => "Server monitoring",
        _ => "Other",
    };
}
