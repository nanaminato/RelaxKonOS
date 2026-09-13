using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Shell;
using RelaxKonOS.Client.Services.Theming;

namespace RelaxKonOS.Client.Services;

/// <summary>Holds user-facing shell appearance + locale state (wallpaper / theme / time / language / region / desktop display).
/// 单例，作为桌面外壳的实时 UI 绑定源（<c>DesktopShellView</c> 绑 <c>Settings.CurrentWallpaper</c> 等）。
/// 数据真源在服务端 <see cref="WorkspacePreferencesDto"/>（Workspace 级，多设备同步）；
/// 本类是其在客户端的活副本——登录时由 <c>PreferencesSync</c> 从服务端加载并 <see cref="Apply"/>，
/// 设置应用编辑后即时 <see cref="Apply"/> 反映到外壳，并 fire-and-forget 保存到服务端。</summary>
public sealed partial class ShellSettings : ObservableObject
{
    public IReadOnlyList<WallpaperOption> Wallpapers { get; }

    [ObservableProperty] private int _wallpaperIndex;
    [ObservableProperty] private ThemeKind _theme = ThemeKind.Light;
    [ObservableProperty] private ThemePreferencesDto _themePreferences = ThemePreferencesDto.Default;
    [ObservableProperty] private string _timeFormat = WorkspacePreferencesDto.TimeFormat24H;
    [ObservableProperty] private string _dateFormat = "yyyy/M/d";
    [ObservableProperty] private string _language;
    [ObservableProperty] private string _region = WorkspacePreferencesDto.Default.Region;
    [ObservableProperty] private string _notepadDefaultEncoding = TextEncodingPreferences.Default;
    [ObservableProperty] private string _codeEditorDefaultEncoding = TextEncodingPreferences.Default;
    // Device-local VSD materializes this selection; the workspace preference schema is extended
    // separately before cross-device synchronization is enabled.
    [ObservableProperty] private string _selectedShellId = ShellApi.DefaultShellId;
    [ObservableProperty] private ShellSelectionDto _shellSelection = new(ShellApi.DefaultShellId);

    // ── 桌面显示配置 ──
    [ObservableProperty] private bool _showBuiltInApps = DesktopDisplaySettingsDto.Default.ShowBuiltInApps;
    [ObservableProperty] private List<string> _visibleAppIds = new(DesktopDisplaySettingsDto.Default.VisibleAppIds);
    [ObservableProperty] private bool _showServerDesktopFiles = DesktopDisplaySettingsDto.Default.ShowServerDesktopFiles;
    [ObservableProperty] private bool _showServerDesktopShortcuts = DesktopDisplaySettingsDto.Default.ShowServerDesktopShortcuts;
    [ObservableProperty] private bool _hasCompletedFirstTimeSetup = DesktopDisplaySettingsDto.Default.HasCompletedFirstTimeSetup;
    [ObservableProperty] private bool _showWindowShadows = DesktopDisplaySettingsDto.Default.ShowWindowShadows;
    [ObservableProperty] private bool _showWindowContentsWhileDragging = DesktopDisplaySettingsDto.Default.ShowWindowContentsWhileDragging;
    [ObservableProperty] private bool _showTaskbarWindowPreviews = DesktopDisplaySettingsDto.Default.ShowTaskbarWindowPreviews;

    private IBrush _currentWallpaper = Brushes.Transparent;
    private string _currentWallpaperKey = WorkspacePreferencesDto.Default.WallpaperKey;
    private Bitmap? _customWallpaper;

    public IBrush CurrentWallpaper => _currentWallpaper;

    /// <summary>当前壁纸的持久 key（含 <c>builtin:</c> 前缀，与服务端 DTO 对齐）。</summary>
    public string CurrentWallpaperKey => _currentWallpaperKey;

    public bool IsCustomWallpaper => _customWallpaper is not null;

    public bool IsDarkTheme => Theme == ThemeKind.Dark;

    private readonly ThemeService _themeService;

    public ShellSettings(ThemeService themeService)
    {
        _themeService = themeService;
        _language = WorkspacePreferencesDto.Default.Language;
        Wallpapers =
        [
            new WallpaperOption("bloom", "Bloom", Gradient("#EAF4FF", "#D7EAFF", "#B9D9F7")),
            new WallpaperOption("aurora", "Aurora", Gradient("#E7F8F2", "#D4F0E7", "#B6DFD2")),
            new WallpaperOption("sunset", "Sunset", Gradient("#FFF0E8", "#FFE1D2", "#F6C5B3")),
            new WallpaperOption("mist", "Mist", Gradient("#F7F7F7", "#E9EDF2", "#D8E0EA")),
            new WallpaperOption("cobalt", "Cobalt", Gradient("#E8F1FF", "#D5E6FF", "#BDD4F5")),
            new WallpaperOption("midnight", "Midnight (Dark)", Gradient("#0B1020", "#172554", "#0F172A")),
            new WallpaperOption("nocturne", "Nocturne (Dark)", Gradient("#1C1530", "#312E5F", "#171225")),
            new WallpaperOption("deep-space", "Deep Space (Dark)", Gradient("#071A2B", "#0C3B5A", "#071827")),
            new WallpaperOption("obsidian", "Obsidian (Dark)", Gradient("#111318", "#242A35", "#101216")),
        ];
        WallpaperIndex = 0;
        SetBuiltInWallpaper(0);
    }

    partial void OnWallpaperIndexChanged(int value)
    {
        // -1 is the explicit "custom image" selection; it must not replace the image brush
        // with a built-in fallback while the asynchronous resource load is in progress.
        if (value >= 0)
            SetBuiltInWallpaper(value);
    }

    partial void OnThemeChanged(ThemeKind value)
    {
        OnPropertyChanged(nameof(IsDarkTheme));
        _themeService.Apply(value, ThemePreferences);
    }

    partial void OnThemePreferencesChanged(ThemePreferencesDto value) => _themeService.Apply(Theme, value);

    partial void OnShowBuiltInAppsChanged(bool value) => NotifyDesktopDisplayChanged();
    partial void OnShowServerDesktopFilesChanged(bool value) => NotifyDesktopDisplayChanged();
    partial void OnShowServerDesktopShortcutsChanged(bool value) => NotifyDesktopDisplayChanged();
    partial void OnVisibleAppIdsChanged(List<string> value) => NotifyDesktopDisplayChanged();
    partial void OnShowWindowShadowsChanged(bool value) => NotifyVisualEffectsChanged();
    partial void OnShowWindowContentsWhileDraggingChanged(bool value) => NotifyVisualEffectsChanged();
    partial void OnShowTaskbarWindowPreviewsChanged(bool value) => NotifyVisualEffectsChanged();

    /// <summary>桌面显示配置变更事件，供 DesktopShellViewModel 订阅以刷新图标。</summary>
    public event EventHandler? DesktopDisplayChanged;
    public event EventHandler? VisualEffectsChanged;
    public event EventHandler<string>? ShellSelectionChanged;

    private void NotifyDesktopDisplayChanged() => DesktopDisplayChanged?.Invoke(this, EventArgs.Empty);
    private void NotifyVisualEffectsChanged() => VisualEffectsChanged?.Invoke(this, EventArgs.Empty);
    partial void OnSelectedShellIdChanged(string value)
    {
        value = ShellApi.ResolveId(value);
        if (!string.Equals(_selectedShellId, value, StringComparison.Ordinal))
        {
            _selectedShellId = value;
            OnPropertyChanged(nameof(SelectedShellId));
        }
        if (!string.Equals(ShellSelection.ShellId, value, StringComparison.Ordinal))
            ShellSelection = new ShellSelectionDto(value, ShellSelection.PackageId, ShellSelection.PackageVersion);
        ShellSelectionChanged?.Invoke(this, value);
    }

    partial void OnShellSelectionChanged(ShellSelectionDto value)
    {
        var normalized = ShellApi.ResolveId(value?.ShellId);
        if (!string.Equals(SelectedShellId, normalized, StringComparison.Ordinal))
            SelectedShellId = normalized;
    }

    /// <summary>将服务端偏好应用到本地活状态（登录加载 / 设置编辑后回写）。</summary>
    public void Apply(WorkspacePreferencesDto prefs)
    {
        PreferencesRevision = prefs.Revision;
        Theme = prefs.Theme;
        ThemePreferences = prefs.ThemePreferences ?? ThemePreferencesDto.Default;
        TimeFormat = prefs.TimeFormat;
        DateFormat = prefs.DateFormat;
        Language = prefs.Language;
        Region = prefs.Region;
        NotepadDefaultEncoding = TextEncodingPreferences.IsSupported(prefs.NotepadDefaultEncoding)
            ? prefs.NotepadDefaultEncoding! : TextEncodingPreferences.Default;
        CodeEditorDefaultEncoding = TextEncodingPreferences.IsSupported(prefs.CodeEditorDefaultEncoding)
            ? prefs.CodeEditorDefaultEncoding! : TextEncodingPreferences.Default;

        // ── 桌面显示配置 ──
        var dd = prefs.DesktopDisplay ?? DesktopDisplaySettingsDto.Default;
        ShowBuiltInApps = dd.ShowBuiltInApps;
        VisibleAppIds = new List<string>(dd.VisibleAppIds ?? new List<string>());
        ShowServerDesktopFiles = dd.ShowServerDesktopFiles;
        ShowServerDesktopShortcuts = dd.ShowServerDesktopShortcuts;
        HasCompletedFirstTimeSetup = dd.HasCompletedFirstTimeSetup;
        ShowWindowShadows = dd.ShowWindowShadows;
        ShowWindowContentsWhileDragging = dd.ShowWindowContentsWhileDragging;
        ShowTaskbarWindowPreviews = dd.ShowTaskbarWindowPreviews;
        var shell = prefs.Shell ?? new ShellSelectionDto(ShellApi.DefaultShellId);
        ShellSelection = new ShellSelectionDto(ShellApi.ResolveId(shell.ShellId), shell.PackageId, shell.PackageVersion);

        if (TryIndexForKey(prefs.WallpaperKey, out var index))
        {
            WallpaperIndex = index;
            // ObservableProperty does not notify when the index is unchanged. Explicitly reset
            // an already-loaded custom image when the server selected the default preset.
            SetBuiltInWallpaper(index);
        }
        else
            SetUnloadedCustomWallpaper(prefs.WallpaperKey);
    }

    /// <summary>导出当前活状态为服务端 DTO（保存时用）。</summary>
    public WorkspacePreferencesDto ToPreferences(IReadOnlyList<DefaultAppMappingDto>? defaultApps = null)
        => new(CurrentWallpaperKey, Theme, TimeFormat, DateFormat, Language, Region,
            defaultApps ?? Array.Empty<DefaultAppMappingDto>(), NotepadDefaultEncoding, CodeEditorDefaultEncoding,
            new DesktopDisplaySettingsDto
            {
                ShowBuiltInApps = ShowBuiltInApps,
                VisibleAppIds = VisibleAppIds,
                ShowServerDesktopFiles = ShowServerDesktopFiles,
                ShowServerDesktopShortcuts = ShowServerDesktopShortcuts,
                HasCompletedFirstTimeSetup = HasCompletedFirstTimeSetup,
                ShowWindowShadows = ShowWindowShadows,
                ShowWindowContentsWhileDragging = ShowWindowContentsWhileDragging,
                ShowTaskbarWindowPreviews = ShowTaskbarWindowPreviews,
            }, ThemePreferences, ShellSelection) { Revision = PreferencesRevision };

    public long? PreferencesRevision { get; private set; }

    /// <summary>Acknowledge only the baseline that was actually submitted; an old response cannot replace a newer load.</summary>
    public void AcknowledgePreferences(long? expectedRevision, long? savedRevision)
    {
        if (PreferencesRevision == expectedRevision) PreferencesRevision = savedRevision;
    }

    /// <summary>快捷方式文件扩展名判定（Windows .lnk / Linux .desktop）。</summary>
    public static bool IsShortcutFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext is "lnk" or "desktop";
    }

    /// <summary>按 key 设置壁纸（来自设置应用的选择）。</summary>
    public bool TrySetWallpaperKey(string key)
    {
        if (!TryIndexForKey(key, out var idx)) return false;
        WallpaperIndex = idx;
        return true;
    }

    /// <summary>用已下载的图片替换当前自定义壁纸的内置回退背景。</summary>
    public void SetCustomWallpaper(string key, Bitmap bitmap)
    {
        if (!key.StartsWith(WorkspacePreferencesDto.CustomWallpaperPrefix, StringComparison.OrdinalIgnoreCase))
        {
            bitmap.Dispose();
            return;
        }
        _customWallpaper?.Dispose();
        _customWallpaper = bitmap;
        WallpaperIndex = -1;
        _currentWallpaperKey = key;
        _currentWallpaper = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
        OnPropertyChanged(nameof(CurrentWallpaper));
        OnPropertyChanged(nameof(CurrentWallpaperKey));
        OnPropertyChanged(nameof(IsCustomWallpaper));
        OnPropertyChanged(nameof(WallpaperIndex));
    }

    private bool TryIndexForKey(string? key, out int index)
    {
        index = 0;
        if (string.IsNullOrWhiteSpace(key)) return false;
        var bare = key.StartsWith(WorkspacePreferencesDto.BuiltInWallpaperPrefix, StringComparison.OrdinalIgnoreCase)
            ? key[WorkspacePreferencesDto.BuiltInWallpaperPrefix.Length..]
            : key;
        for (var i = 0; i < Wallpapers.Count; i++)
            if (string.Equals(Wallpapers[i].Key, bare, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                return true;
            }
        return false;
    }

    private void SetBuiltInWallpaper(int index)
    {
        if (index < 0 || index >= Wallpapers.Count) index = 0;
        _customWallpaper?.Dispose();
        _customWallpaper = null;
        _currentWallpaperKey = WorkspacePreferencesDto.BuiltInWallpaperPrefix + Wallpapers[index].Key;
        _currentWallpaper = Wallpapers[index].Brush;
        OnPropertyChanged(nameof(CurrentWallpaper));
        OnPropertyChanged(nameof(CurrentWallpaperKey));
        OnPropertyChanged(nameof(IsCustomWallpaper));
    }

    private void SetUnloadedCustomWallpaper(string key)
    {
        _customWallpaper?.Dispose();
        _customWallpaper = null;
        WallpaperIndex = -1;
        _currentWallpaperKey = key;
        // A download failure must not leave the desktop blank; retain a deterministic built-in fallback.
        _currentWallpaper = Wallpapers[0].Brush;
        OnPropertyChanged(nameof(CurrentWallpaper));
        OnPropertyChanged(nameof(CurrentWallpaperKey));
        OnPropertyChanged(nameof(IsCustomWallpaper));
        OnPropertyChanged(nameof(WallpaperIndex));
    }

    private static IBrush Gradient(string c0, string c1, string c2)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        };
        brush.GradientStops.Add(new GradientStop(Color.Parse(c0), 0));
        brush.GradientStops.Add(new GradientStop(Color.Parse(c1), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.Parse(c2), 1));
        return brush;
    }

}
