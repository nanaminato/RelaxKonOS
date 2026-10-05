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
            Workspace("workspace.colors", "personalization/colors", "settings.colors_and_mode", "settings.colors_and_mode.description", "enum", ["theme", "主题", "テーマ"]),
            Workspace("workspace.wallpaper", "personalization/background", "settings.wallpaper", "settings.wallpaper.description", "image", ["wallpaper", "壁纸", "壁紙"]),
            Workspace("workspace.desktopLayout", "personalization/layout", "settings.desktop_layout", "settings.desktop_layout", "shell", ["desktop", "桌面", "デスクトップ"]),
            Workspace("workspace.systemStyle", "personalization/style", "settings.system_style", "settings.system_style", "enum", ["style", "风格", "スタイル"]),
            Workspace("workspace.palette", "personalization/colors", "settings.palette", "settings.palette.description", "enum", ["palette", "调色板", "配色"]),
            Workspace("workspace.accent", "personalization/colors", "settings.accent", "settings.accent.hint", "color", ["accent", "强调色", "アクセント"]),
            Workspace("workspace.customTheme", "personalization/colors", "settings.custom_theme", "settings.custom_theme.description", "palette", ["import", "export", "导入", "导出", "インポート"]),
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
        // The platform decides the effective state and the name limit; discovery only reports what the
        // caller can already reach, so it never probes the Helper on a catalog request.
        var identityCapability = !OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()
            ? new SettingsCapability(SettingsCapabilityState.PlatformUnsupported, "settings.identity.platform_unsupported")
            : OperatingSystem.IsLinux() && (string.IsNullOrWhiteSpace(helper.HelperPath) || !File.Exists(helper.HelperPath))
                ? new(SettingsCapabilityState.HelperUnavailable, "settings.helper.not_installed")
                : !grants.IsGranted(principal, HostElevationCapability.HostIdentityChange, HostIdentityOperationCoordinator.IdentityResource)
                    ? new(SettingsCapabilityState.ElevationRequired, "settings.elevation_required")
                    : new(SettingsCapabilityState.Available);
        items.Add(new("host.identity.hostname", "system", "settings.hostname", "settings.hostname.scope",
            "relaxkonos://settings/system", SettingsScope.HostMachine, "hostName", identityCapability,
            OperatingSystem.IsWindows() ? SettingsEffectiveState.HostRestart : SettingsEffectiveState.Immediate,
            ["hostname", "computer name", "主机名", "计算机名", "ホスト名", "コンピューター名"]));
        return new(items, DateTimeOffset.UtcNow);
    }

    private static SettingDescriptor Workspace(string id, string route, string title, string description, string type, string[] keywords) =>
        new(id, route, title, description, "relaxkonos://settings/" + route, SettingsScope.Workspace, type,
            new(SettingsCapabilityState.Available), SettingsEffectiveState.Immediate, keywords);
}
