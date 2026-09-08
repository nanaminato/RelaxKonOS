using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Desktop;

/// <summary>
/// 桌面显示项目配置。
/// 控制桌面上显示哪些元素：内置应用、服务器桌面一般文件、服务器桌面快捷方式。
/// 作为 WorkspacePreferencesDto 的一部分持久化（ToJson 单列存储）。
/// </summary>
public sealed record DesktopDisplaySettingsDto
{
    /// <summary>是否显示内置应用程序（默认 true）。</summary>
    [property: JsonPropertyName("showBuiltInApps")]
    public bool ShowBuiltInApps { get; set; } = true;

    /// <summary>
    /// 需要显示在桌面上的内置应用 ID 列表。
    /// 当 ShowBuiltInApps = true 且此列表非空时，仅显示列表中的应用；
    /// 当 ShowBuiltInApps = true 且此列表为空时，显示全部内置应用；
    /// 当 ShowBuiltInApps = false 时，此列表忽略，不显示任何内置应用。
    /// </summary>
    [property: JsonPropertyName("visibleAppIds")]
    public List<string> VisibleAppIds { get; set; } = [];

    /// <summary>是否显示服务器桌面一般文件（非快捷方式，默认 true）。</summary>
    [property: JsonPropertyName("showServerDesktopFiles")]
    public bool ShowServerDesktopFiles { get; set; } = true;

    /// <summary>是否显示服务器桌面快捷方式（默认 false）。</summary>
    [property: JsonPropertyName("showServerDesktopShortcuts")]
    public bool ShowServerDesktopShortcuts { get; set; } = false;

    /// <summary>用户是否已完成首次桌面配置（跳过也算完成）。用于判断是否弹出首次配置引导。</summary>
    [property: JsonPropertyName("hasCompletedFirstTimeSetup")]
    public bool HasCompletedFirstTimeSetup { get; set; } = false;

    /// <summary>默认配置（全部内置应用 + 一般文件显示，快捷方式不显示，未完成首次配置）。</summary>
    // Return a fresh object because it is an EF-owned JSON object updated in place.
    public static DesktopDisplaySettingsDto Default => new()
    {
        ShowBuiltInApps = true,
        VisibleAppIds = [],
        ShowServerDesktopFiles = true,
        ShowServerDesktopShortcuts = false,
        HasCompletedFirstTimeSetup = false,
    };
}
