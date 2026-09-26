using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Desktop;

namespace RelaxKonOS.Protocol.Workspace;

/// <summary>
/// Workspace 级用户偏好（壁纸 / 桌面体验 / 时间格式 / 日期格式 / 语言 / 区域 / 默认程序 / 桌面显示配置）。
/// 与 <see cref="TerminalSettingsDto"/> / <see cref="RelaxKonOS.Protocol.Browser.BrowserSettingsDto"/> 同模式：
/// 真源为 Workspace Desktop 注册表键；持久化状态来自注册表存储。
/// 多设备登录同一 Workspace 时共享同一份偏好。
/// 颜色/深浅模式、系统风格与桌面 Shell 三者统一收在 <see cref="DesktopExperience"/> 下，
/// 不再有并行可读写的旧字段。
/// </summary>
public sealed record WorkspacePreferencesDto
{
    /// <summary>Observed registry revision. Required on writes; never synthesize a fresh baseline for an old draft.</summary>
    [JsonPropertyName("revision")]
    public long? Revision { get; set; }

    /// <summary>Server-observed durable revision; null while persistence is pending.</summary>
    [JsonPropertyName("persistedRevision")]
    public long? PersistedRevision { get; set; }

    [JsonPropertyName("wallpaperKey")]
    public string WallpaperKey { get; set; }

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

    /// <summary>Colors, system style and shell layout. The only read/write path for any of the three.</summary>
    [JsonPropertyName("desktopExperience")]
    public DesktopExperiencePreferencesDto? DesktopExperience { get; set; }

    public WorkspacePreferencesDto(
        string WallpaperKey,
        string TimeFormat,
        string DateFormat,
        string Language,
        string Region,
        IReadOnlyList<DefaultAppMappingDto>? DefaultApps,
        string? NotepadDefaultEncoding = TextEncodingPreferences.Default,
        string? CodeEditorDefaultEncoding = TextEncodingPreferences.Default,
        DesktopDisplaySettingsDto? DesktopDisplay = null,
        DesktopExperiencePreferencesDto? DesktopExperience = null)
    {
        this.WallpaperKey = WallpaperKey;
        this.TimeFormat = TimeFormat;
        this.DateFormat = DateFormat;
        this.Language = Language;
        this.Region = Region;
        this.DefaultApps = DefaultApps?.ToList() ?? [];
        this.NotepadDefaultEncoding = NotepadDefaultEncoding;
        this.CodeEditorDefaultEncoding = CodeEditorDefaultEncoding;
        this.DesktopDisplay = DesktopDisplay ?? DesktopDisplaySettingsDto.Default;
        this.DesktopExperience = DesktopExperience ?? DesktopExperiencePreferencesDto.Default;
    }

    // Both EF Core and System.Text.Json must use the parameterless constructor. JSON cannot
    // bind the public constructor's IReadOnlyList parameter to the mutable List property,
    // while property-based deserialization preserves the wire contract for DefaultApps.
    public WorkspacePreferencesDto()
        : this(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
            [], TextEncodingPreferences.Default, TextEncodingPreferences.Default,
            DesktopDisplaySettingsDto.Default, DesktopExperiencePreferencesDto.Default)
    {
    }

    /// <summary>24 小时制标识。</summary>
    public const string TimeFormat24H = "24h";

    /// <summary>12 小时制标识。</summary>
    public const string TimeFormat12H = "12h";

    /// <summary>
    /// Use the client device's UI language. Clients map Chinese to <c>zh-CN</c>, Japanese to
    /// <c>ja-JP</c>, and every other system language to <c>en-US</c>.
    /// </summary>
    public const string LanguageFollowSystem = "follow-system";

    /// <summary>内置壁纸 key 前缀（客户端预设目录使用）。</summary>
    public const string BuiltInWallpaperPrefix = "builtin:";

    /// <summary>Workspace 托管图片壁纸的 key 前缀。前缀后的值是服务端生成的 blob id，
    /// 因此不会把宿主机路径暴露或同步到其他设备。</summary>
    public const string CustomWallpaperPrefix = "custom:";

    // This must be a fresh object: tracked SQLite owned entities are mutated in place.
    public static WorkspacePreferencesDto Default => new(
        WallpaperKey: BuiltInWallpaperPrefix + "bloom",
        TimeFormat: TimeFormat24H,
        DateFormat: "yyyy/M/d",
        Language: "en-US",
        Region: "en-US",
        DefaultApps: [],
        NotepadDefaultEncoding: TextEncodingPreferences.Default,
        CodeEditorDefaultEncoding: TextEncodingPreferences.Default,
        DesktopDisplay: DesktopDisplaySettingsDto.Default,
        DesktopExperience: DesktopExperiencePreferencesDto.Default);
}
