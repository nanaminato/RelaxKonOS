using System.Text.Json.Serialization;
using RemoteOS.Protocol.Desktop;

namespace RemoteOS.Protocol.Workspace;

/// <summary>
/// Workspace 级用户偏好（壁纸 / 主题 / 时间格式 / 日期格式 / 语言 / 区域 / 默认程序 / 桌面显示配置）。
/// 与 <see cref="TerminalSettingsDto"/> / <see cref="RemoteOS.Protocol.Browser.BrowserSettingsDto"/> 同模式：
/// 作为 <c>OwnsOne + ToJson</c> 挂在 Workspace 上，单列 JSON 文本持久化（新增字段无需改 schema）。
/// 多设备登录同一 Workspace 时共享同一份偏好。
/// </summary>
public sealed record WorkspacePreferencesDto
{
    [JsonPropertyName("wallpaperKey")]
    public string WallpaperKey { get; set; }

    [JsonPropertyName("theme")]
    public ThemeKind Theme { get; set; }

    [JsonPropertyName("timeFormat")]
    public string TimeFormat { get; set; }

    [JsonPropertyName("dateFormat")]
    public string DateFormat { get; set; }

    [JsonPropertyName("language")]
    public string Language { get; set; }

    [JsonPropertyName("region")]
    public string Region { get; set; }

    // Keep the owned JSON collection mutable. EF Core identifies its items by a synthesized
    // ordinal, so replacing this navigation would attempt to rewrite those key values.
    [JsonPropertyName("defaultApps")]
    public List<DefaultAppMappingDto> DefaultApps { get; set; }

    [JsonPropertyName("notepadDefaultEncoding")]
    public string? NotepadDefaultEncoding { get; set; }

    [JsonPropertyName("codeEditorDefaultEncoding")]
    public string? CodeEditorDefaultEncoding { get; set; }

    [JsonPropertyName("desktopDisplay")]
    public DesktopDisplaySettingsDto? DesktopDisplay { get; set; }

    [JsonPropertyName("themePreferences")]
    public ThemePreferencesDto? ThemePreferences { get; set; }

    [JsonPropertyName("shellId")]
    public string? ShellId { get; set; }

    public WorkspacePreferencesDto(
        string WallpaperKey,
        ThemeKind Theme,
        string TimeFormat,
        string DateFormat,
        string Language,
        string Region,
        IReadOnlyList<DefaultAppMappingDto>? DefaultApps,
        string? NotepadDefaultEncoding = TextEncodingPreferences.Default,
        string? CodeEditorDefaultEncoding = TextEncodingPreferences.Default,
        DesktopDisplaySettingsDto? DesktopDisplay = null,
        ThemePreferencesDto? ThemePreferences = null,
        string? ShellId = "remoteos")
    {
        this.WallpaperKey = WallpaperKey;
        this.Theme = Theme;
        this.TimeFormat = TimeFormat;
        this.DateFormat = DateFormat;
        this.Language = Language;
        this.Region = Region;
        this.DefaultApps = DefaultApps?.ToList() ?? [];
        this.NotepadDefaultEncoding = NotepadDefaultEncoding;
        this.CodeEditorDefaultEncoding = CodeEditorDefaultEncoding;
        this.DesktopDisplay = DesktopDisplay ?? DesktopDisplaySettingsDto.Default;
        this.ThemePreferences = ThemePreferences ?? ThemePreferencesDto.Default;
        this.ShellId = ShellId;
    }

    // Both EF Core and System.Text.Json must use the parameterless constructor. JSON cannot
    // bind the public constructor's IReadOnlyList parameter to the mutable List property,
    // while property-based deserialization preserves the wire contract for DefaultApps.
    public WorkspacePreferencesDto()
        : this(string.Empty, default, string.Empty, string.Empty, string.Empty, string.Empty,
            [], TextEncodingPreferences.Default, TextEncodingPreferences.Default,
            DesktopDisplaySettingsDto.Default, ThemePreferencesDto.Default, "remoteos")
    {
    }

    /// <summary>24 小时制标识。</summary>
    public const string TimeFormat24H = "24h";

    /// <summary>12 小时制标识。</summary>
    public const string TimeFormat12H = "12h";

    /// <summary>内置壁纸 key 前缀（客户端预设目录使用）。</summary>
    public const string BuiltInWallpaperPrefix = "builtin:";

    /// <summary>Workspace 托管图片壁纸的 key 前缀。前缀后的值是服务端生成的 blob id，
    /// 因此不会把宿主机路径暴露或同步到其他设备。</summary>
    public const string CustomWallpaperPrefix = "custom:";

    // This must be a fresh object: tracked SQLite owned entities are mutated in place.
    public static WorkspacePreferencesDto Default => new(
        WallpaperKey: BuiltInWallpaperPrefix + "bloom",
        Theme: ThemeKind.Light,
        TimeFormat: TimeFormat24H,
        DateFormat: "yyyy/M/d",
        Language: "en-US",
        Region: "en-US",
        DefaultApps: [],
        NotepadDefaultEncoding: TextEncodingPreferences.Default,
        CodeEditorDefaultEncoding: TextEncodingPreferences.Default,
        DesktopDisplay: DesktopDisplaySettingsDto.Default,
        ThemePreferences: ThemePreferencesDto.Default,
        ShellId: "remoteos");
}
