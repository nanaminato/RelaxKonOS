using System.Security.Claims;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Settings;

/// <summary>Discovery metadata is local and fast; domain reads perform live platform capability probes.</summary>
public sealed class SettingsCatalog(PrivilegedHelperOptions helper, IHostElevationSessionStore grants)
{
    public SettingsCatalogSnapshot Read(ClaimsPrincipal principal)
    {
        var items = new List<SettingDescriptor>
        {
            Workspace("workspace.theme", "personalization", "settings.theme", "settings.theme.description", "enum", ["theme", "主题", "テーマ"]),
            Workspace("workspace.wallpaper", "personalization", "settings.wallpaper", "settings.wallpaper.description", "image", ["wallpaper", "壁纸", "壁紙"]),
            Workspace("workspace.shell", "personalization", "settings.shell", "settings.shell.description", "shell", ["desktop", "桌面", "デスクトップ"]),
            Workspace("workspace.timeFormat", "time-language", "settings.time.format", "settings.language_region.description", "enum", ["clock", "时钟", "時計"]),
            Workspace("workspace.defaultApps", "default-apps", "settings.default_apps", "settings.default_apps.description", "mapping", ["association", "关联", "関連付け"]),
        };
        var capability = !OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()
            ? new SettingsCapability(SettingsCapabilityState.PlatformUnsupported, "settings.time.platform_unsupported")
            : OperatingSystem.IsLinux() && (string.IsNullOrWhiteSpace(helper.HelperPath) || !File.Exists(helper.HelperPath))
                ? new(SettingsCapabilityState.HelperUnavailable, "settings.helper.not_installed")
                : !grants.IsGranted(principal, HostElevationCapability.HostTimeChange, SettingsOperationCoordinator.TimeResource)
                    ? new(SettingsCapabilityState.ElevationRequired, "settings.elevation_required")
                    : new(SettingsCapabilityState.Available);
        items.Add(new("host.time.zone", "time-language", "settings.time_zone", "settings.host_time.scope",
            "relaxkonos://settings/time-language", SettingsScope.HostMachine, "timeZoneId", capability,
            SettingsEffectiveState.Immediate, ["timezone", "time zone", "时区", "タイムゾーン"]));
        return new(items, DateTimeOffset.UtcNow);
    }

    private static SettingDescriptor Workspace(string id, string route, string title, string description, string type, string[] keywords) =>
        new(id, route, title, description, "relaxkonos://settings/" + route, SettingsScope.Workspace, type,
            new(SettingsCapabilityState.Available), SettingsEffectiveState.Immediate, keywords);
}
