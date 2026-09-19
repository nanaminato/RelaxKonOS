using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Client.Localization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Protocol.Workspace.SystemStyles;
using RelaxKonOS.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>「个性化」页，由三块相互独立的设置组成：
/// <list type="number">
/// <item>颜色与模式：浅/深/跟随系统 + 调色板 + 强调色 + 自定义主题；</item>
/// <item>系统风格：窗口外壳、菜单、任务概览与桌面 chrome 的形态/尺寸/动效；</item>
/// <item>桌面布局：选哪个 Shell 实现桌面，以及是否采用该 Shell 推荐的系统风格。</item>
/// </list>
/// 三者是独立的用户意图：切换颜色不会改变菜单布局，切换风格不会改写调色板，
/// 而“采用推荐风格”始终是一次显式、可撤销的操作。
/// 透传读写 <see cref="ShellSettings"/>，改动即时反映到桌面并触发保存。</summary>
public sealed partial class PersonalizationPageViewModel : SettingsPageViewModel
{
    private readonly IShellCatalog _shellCatalog;
    private readonly ISystemStyleRegistry _systemStyles;
    private readonly LocalizationService _localization;
    private IReadOnlyList<ShellChoice> _shellChoices = [];
    private IReadOnlyList<SystemStyleChoice> _systemStyleChoices = [];

    public PersonalizationPageViewModel(
        ShellSettings settings,
        Action? save,
        IShellCatalog? shellCatalog = null,
        ISystemStyleRegistry? systemStyles = null) : base(settings, save)
    {
        _localization = RelaxKonOS.Client.App.Services.GetRequiredService<LocalizationService>();
        _shellCatalog = shellCatalog ?? RelaxKonOS.Client.App.Services.GetRequiredService<IShellCatalog>();
        _systemStyles = systemStyles ?? RelaxKonOS.Client.App.Services.GetRequiredService<ISystemStyleRegistry>();
        RefreshShellChoices();
        RefreshSystemStyleChoices();
        _shellCatalog.Changed += (_, _) => RefreshShellChoices();
        _systemStyles.Changed += (_, _) => RefreshSystemStyleChoices();
        _localization.LanguageChanged += (_, _) => { RefreshShellChoices(); RefreshSystemStyleChoices(); };
        // Theme 变化（含外部 Apply 加载）时刷新三个 RadioButton 绑定。
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellSettings.Appearance))
            {
                OnPropertyChanged(nameof(Theme));
                OnPropertyChanged(nameof(IsLightTheme));
                OnPropertyChanged(nameof(IsDarkTheme));
                OnPropertyChanged(nameof(IsSystemTheme));
                OnPropertyChanged(nameof(PalettePreview));
                OnPropertyChanged(nameof(PaletteId));
                AccentInput = Settings.Appearance.AccentOverride ?? string.Empty;
                OnPropertyChanged(nameof(PaletteChoices));
                OnPropertyChanged(nameof(SelectedCustomPalette));
                OnPropertyChanged(nameof(HasSelectedCustomPalette));
                OnPropertyChanged(nameof(HasAccentOverride));
            }
            else if (e.PropertyName == nameof(ShellSettings.SystemStyleId))
            {
                OnPropertyChanged(nameof(SelectedSystemStyleId));
                OnPropertyChanged(nameof(SystemStyleSummary));
                OnPropertyChanged(nameof(SystemStyleProblem));
                OnPropertyChanged(nameof(HasSystemStyleProblem));
                OnPropertyChanged(nameof(IsUsingRecommendedStyle));
            }
            else if (e.PropertyName == nameof(ShellSettings.SelectedShellId))
            {
                // Preferences may arrive after Settings is already open. Keep the ComboBox in
                // sync without treating that inbound update as another user selection.
                OnPropertyChanged(nameof(SelectedShellId));
                OnPropertyChanged(nameof(IsUsingRecommendedStyle));
            }
        };
        _accentInput = Settings.Appearance.AccentOverride ?? string.Empty;
    }

    public override string Route => "personalization";
    public override string DisplayNameKey => "settings.page.personalization";
    public override string DisplayName => "Personalization";

    // ── 颜色与模式 ──────────────────────────────────────────────────────────────

    public IReadOnlyList<RelaxKonOS.Client.Services.WallpaperOption> Wallpapers => Settings.Wallpapers;

    public ThemeKind Theme
    {
        get => Settings.Appearance.Mode;
        set
        {
            if (Settings.Appearance.Mode == value) return;
            Settings.Appearance = Settings.Appearance with { Mode = value };
            Save();
        }
    }

    public bool IsLightTheme { get => Theme == ThemeKind.Light; set { if (value) Theme = ThemeKind.Light; } }
    public bool IsDarkTheme { get => Theme == ThemeKind.Dark; set { if (value) Theme = ThemeKind.Dark; } }
    public bool IsSystemTheme { get => Theme == ThemeKind.System; set { if (value) Theme = ThemeKind.System; } }

    /// <summary>Built-ins deliberately share the same semantic token contract in every RelaxKonOS app.</summary>
    public IReadOnlyList<ThemePaletteChoice> PaletteChoices =>
    [
        new("builtin:relaxkonos-blue", "RelaxKonOS Blue", false),
        new("builtin:nord", "Nord", false),
        new("builtin:catppuccin", "Catppuccin", false),
        .. (Settings.Appearance.CustomPalettes ?? [])
            .Select(palette => new ThemePaletteChoice("custom:" + palette.Id, palette.Name, true)),
    ];

    public string PaletteId
    {
        get => Settings.Appearance.PaletteId;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == Settings.Appearance.PaletteId) return;
            UpdateAppearance(value, Settings.Appearance.AccentOverride);
        }
    }

    /// <summary>Supplied by the Avalonia page so the VM never accesses a TopLevel or filesystem picker.</summary>
    public Func<Task>? RequestCustomWallpaperAsync { get; set; }
    public Func<Task>? RequestThemeImportAsync { get; set; }
    public Func<ThemePaletteDto, Task>? RequestThemeExportAsync { get; set; }
    public Func<ThemePaletteDto, Task<bool>>? RequestThemeDeletionConfirmationAsync { get; set; }

    public int WallpaperIndex
    {
        get => Settings.WallpaperIndex;
        set { Settings.WallpaperIndex = value; Save(); }
    }

    private string _accentInput = string.Empty;
    private string? _accentError;

    /// <summary>The editable accent text. Invalid values stay visible instead of being silently discarded.</summary>
    public string AccentInput
    {
        get => _accentInput;
        set
        {
            value ??= string.Empty;
            if (_accentInput == value) return;
            _accentInput = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAccentOverride));
            var color = string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
            if (color is not null && !ThemePaletteDefaults.IsColor(color))
            {
                AccentError = T("settings.accent.invalid", "Enter a valid #RRGGBB or #AARRGGBB value.");
                return;
            }
            AccentError = null;
            if (color == Settings.Appearance.AccentOverride) return;
            UpdateAppearance(Settings.Appearance.PaletteId, color);
        }
    }

    public string? AccentError
    {
        get => _accentError;
        private set
        {
            if (_accentError == value) return;
            _accentError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAccentError));
        }
    }

    public bool HasAccentError => !string.IsNullOrEmpty(AccentError);
    public bool HasAccentOverride => !string.IsNullOrWhiteSpace(AccentInput);

    /// <summary>Small live swatch strip for the selected palette and its current accent override.</summary>
    public ThemePalettePreview PalettePreview
    {
        get
        {
            var dark = Theme == ThemeKind.Dark;
            var colors = ThemePaletteDefaults.Resolve(Settings.Appearance, dark);
            return new ThemePalettePreview(
                Brush(colors["AppBackground"]), Brush(colors["Surface"]), Brush(colors["Accent"]),
                Brush(colors["Success"]), Brush(colors["Danger"]), colors["Accent"]);
        }
    }

    public ThemePaletteDto? SelectedCustomPalette => Settings.Appearance.PaletteId.StartsWith("custom:", StringComparison.Ordinal)
        ? Settings.Appearance.CustomPalettes?.FirstOrDefault(p => p.Id == Settings.Appearance.PaletteId["custom:".Length..])
        : null;

    public bool HasSelectedCustomPalette => SelectedCustomPalette is not null;

    private void UpdateAppearance(string paletteId, string? accent)
    {
        Settings.Appearance = Settings.Appearance with { PaletteId = paletteId, AccentOverride = accent };
        OnPropertyChanged(nameof(PaletteId));
        OnPropertyChanged(nameof(AccentInput));
        OnPropertyChanged(nameof(HasAccentOverride));
        OnPropertyChanged(nameof(PalettePreview));
        Save();
    }

    /// <summary>Adds a validated imported palette and selects it. Only serialisable colour tokens are accepted.</summary>
    public bool TryImportCustomPalette(ThemePaletteDto? source, out string? error)
    {
        error = null;
        if (source is null || source.FormatVersion != 2
            || string.IsNullOrWhiteSpace(source.Name) || source.Name.Trim().Length > 80)
        {
            error = T("settings.theme_import.invalid", "This file does not contain a valid RelaxKonOS theme.");
            return false;
        }

        var existing = Settings.Appearance.CustomPalettes ?? [];
        if (existing.Count >= 20)
        {
            error = T("settings.theme_import.limit", "You can keep up to 20 custom themes.");
            return false;
        }

        if (!ThemePaletteImport.TryNormalize(source, existing.Select(p => p.Id), Settings.Appearance.AccentOverride,
                out var imported, out var importError))
        {
            error = importError == ThemePaletteImportError.Inaccessible
                ? T("settings.theme_import.inaccessible", "This theme does not meet the contrast requirements.")
                : T("settings.theme_import.invalid", "This file does not contain a valid RelaxKonOS theme.");
            return false;
        }
        Settings.Appearance = Settings.Appearance with
        {
            PaletteId = "custom:" + imported!.Id,
            CustomPalettes = [.. existing, imported],
        };
        Save();
        return true;
    }

    public void DeleteSelectedCustomPalette()
    {
        var selected = SelectedCustomPalette;
        if (selected is null) return;
        var remaining = Settings.Appearance.CustomPalettes?.Where(p => p.Id != selected.Id).ToList() ?? [];
        Settings.Appearance = Settings.Appearance with
        {
            PaletteId = AppearancePreferencesDto.DefaultPaletteId,
            CustomPalettes = remaining,
        };
        Save();
    }

    // ── 系统风格 ────────────────────────────────────────────────────────────────

    /// <summary>Device-local list; a style the workspace asked for but this device lacks still appears.</summary>
    public IReadOnlyList<SystemStyleChoice> SystemStyleChoices => _systemStyleChoices;

    /// <summary>Cross-device intent. Changing it re-applies tokens without touching the palette.</summary>
    public string SelectedSystemStyleId
    {
        get => Settings.SystemStyleId;
        set
        {
            // A ComboBox writes null/empty while rebuilding its containers; that is a UI
            // transition, not a request to fall back to the default style.
            if (string.IsNullOrWhiteSpace(value) || value == Settings.SystemStyleId) return;
            Settings.SystemStyleId = value;
            Save();
        }
    }

    /// <summary>Explains an unresolvable selection instead of silently substituting another style.</summary>
    public string? SystemStyleProblem
    {
        get
        {
            if (_systemStyles.TryGetManifest(Settings.SystemStyleId, out _, out var problem)) return null;
            return T(problem ?? SystemStyleProblems.StyleUnavailable,
                "This device cannot render the selected system style.");
        }
    }

    public bool HasSystemStyleProblem => !string.IsNullOrEmpty(SystemStyleProblem);

    /// <summary>Human-readable summary of the tokens a user actually notices when a style changes.</summary>
    public string SystemStyleSummary
    {
        get
        {
            if (!_systemStyles.TryGetManifest(Settings.SystemStyleId, out var manifest, out _))
                return string.Empty;
            var tokens = manifest.ResolveTokens(Settings.Appearance.Mode == ThemeKind.Dark);
            return string.Join(" · ", new[]
            {
                Token(tokens, "WindowTitleBarHeight", "settings.system_style.token.title_bar", "Title bar"),
                Token(tokens, "WindowCornerRadius", "settings.system_style.token.window_radius", "Window corners"),
                Token(tokens, "MenuCornerRadius", "settings.system_style.token.menu_radius", "Menu corners"),
                Token(tokens, "TaskbarHeight", "settings.system_style.token.taskbar", "Taskbar"),
                Token(tokens, "MinimumHitTarget", "settings.system_style.token.hit_target", "Minimum target"),
            });
        }
    }

    /// <summary>True when the current style already matches what the selected shell recommends.</summary>
    public bool IsUsingRecommendedStyle =>
        string.Equals(Settings.SystemStyleId, RecommendedSystemStyleId, StringComparison.Ordinal);

    private string RecommendedSystemStyleId => SystemStyleIds.RecommendedForShell(Settings.SelectedShellId);

    /// <summary>
    /// Shell and style stay separate stored choices; this is the explicit, reversible convenience
    /// action the settings page offers instead of coupling them in the data model.
    /// </summary>
    [RelayCommand]
    private void ApplyRecommendedStyle()
    {
        if (IsUsingRecommendedStyle) return;
        Settings.SystemStyleId = RecommendedSystemStyleId;
        Save();
    }

    private string Token(IReadOnlyDictionary<string, double> tokens, string key, string labelKey, string fallback)
    {
        var value = tokens.TryGetValue(key, out var declared)
            ? declared
            : SystemStyleTokenContract.DefaultOf(key);
        return $"{T(labelKey, fallback)} {value:0.#}";
    }

    private void RefreshSystemStyleChoices()
    {
        var next = _systemStyles.Available
            .Select(style => new SystemStyleChoice(
                style.Id,
                LocalizeSystemStyleName(style.Id, style.SelectionDisplayName),
                style.IsBuiltIn,
                style.UnavailableReason is { } reason
                    ? T(reason, "This device cannot render this system style.")
                    : null))
            .ToArray();
        if (_systemStyleChoices.SequenceEqual(next)) return;
        _systemStyleChoices = next;
        OnPropertyChanged(nameof(SystemStyleChoices));
        OnPropertyChanged(nameof(SystemStyleSummary));
        OnPropertyChanged(nameof(SystemStyleProblem));
        OnPropertyChanged(nameof(HasSystemStyleProblem));
        OnPropertyChanged(nameof(IsUsingRecommendedStyle));
    }

    private string LocalizeSystemStyleName(string id, string fallback) => id switch
    {
        SystemStyleIds.WindowsLike => T("settings.system_style.windows_like", "Windows-like"),
        SystemStyleIds.MacOsLike => T("settings.system_style.macos_like", "macOS-like"),
        SystemStyleIds.UbuntuLike => T("settings.system_style.ubuntu_like", "Ubuntu-like"),
        _ => fallback,
    };

    // ── 桌面布局（Shell）────────────────────────────────────────────────────────

    /// <summary>
    /// A stable snapshot is important here: Avalonia briefly clears SelectedValue while a
    /// ComboBox receives a new ItemsSource. Recreating this list on every getter made that
    /// transient null flow back into the shell setting and repeatedly swap the desktop shell.
    /// </summary>
    public IReadOnlyList<ShellChoice> ShellChoices => _shellChoices;

    /// <summary>Device-local presentation choice; changing it immediately swaps only the shell view.</summary>
    public string SelectedShellId
    {
        get => Settings.SelectedShellId;
        set
        {
            // ComboBox writes null/empty while rebuilding its item containers. That is a UI
            // transition, not a request to select the default shell.
            if (string.IsNullOrWhiteSpace(value)) return;

            var id = ShellApi.ResolveId(value);
            if (!_shellCatalog.TryGet(id, out var shell) || !shell.IsAvailable) return;
            // Store the package identity along with the cross-device shell intent.  Resolving
            // remains device-local, but retaining this metadata prevents an external shell
            // choice from being reduced to a bare ID on the next launch.
            if (Settings.ShellSelection.ShellId == id
                && Settings.ShellSelection.PackageId == shell.PackageId
                && Settings.ShellSelection.PackageVersion == shell.Version) return;
            Settings.ShellSelection = new ShellSelectionDto(id, shell.PackageId, shell.Version);
            Save();
        }
    }

    private void RefreshShellChoices()
    {
        var next = _shellCatalog.Available
            .Select(definition => new ShellChoice(
                definition.Id,
                LocalizeShellName(definition.Id, definition.DisplayName),
                definition.Source,
                definition.Version,
                definition.UnavailableReason))
            .ToArray();
        if (_shellChoices.SequenceEqual(next)) return;

        _shellChoices = next;
        OnPropertyChanged(nameof(ShellChoices));
    }

    private string LocalizeShellName(string id, string fallback) => id switch
    {
        "relaxkonos.windows-like" => T("settings.shell.windows_like", "Windows-style desktop"),
        "relaxkonos.macos-like" => T("settings.shell.macos_like", "macOS-style desktop"),
        "relaxkonos.ubuntu-like" => T("settings.shell.ubuntu_like", "Ubuntu-style desktop"),
        _ => fallback,
    };

    [RelayCommand]
    private void ResetAccent() => AccentInput = string.Empty;

    [RelayCommand]
    private async Task ImportThemeAsync()
    {
        if (RequestThemeImportAsync is not null) await RequestThemeImportAsync();
    }

    [RelayCommand]
    private async Task ExportThemeAsync()
    {
        if (SelectedCustomPalette is { } palette && RequestThemeExportAsync is not null)
            await RequestThemeExportAsync(palette);
    }

    [RelayCommand]
    private async Task DeleteThemeAsync()
    {
        if (SelectedCustomPalette is not { } palette) return;
        if (RequestThemeDeletionConfirmationAsync is null || await RequestThemeDeletionConfirmationAsync(palette))
            DeleteSelectedCustomPalette();
    }

    [RelayCommand]
    private async Task ChooseImageAsync()
    {
        if (RequestCustomWallpaperAsync is not null)
            await RequestCustomWallpaperAsync();
    }

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));

}

public sealed record ShellChoice(string Id, string DisplayName, ShellSourceKind Source = ShellSourceKind.BuiltIn,
    string? Version = null, string? UnavailableReason = null)
{
    public bool HasUnavailableReason => !string.IsNullOrWhiteSpace(UnavailableReason);
    public string SelectionDisplayName => Id == ShellApi.DefaultShellId
        ? string.Format(LocalizedText.Get("settings.shell.default_format", "{0} (Default)"), DisplayName)
        : DisplayName;
}

/// <summary>One selectable system style. Unavailable entries stay visible with their reason.</summary>
public sealed record SystemStyleChoice(string Id, string DisplayName, bool IsBuiltIn, string? UnavailableReason = null)
{
    public bool HasUnavailableReason => !string.IsNullOrWhiteSpace(UnavailableReason);
    public string SelectionDisplayName => string.IsNullOrWhiteSpace(UnavailableReason)
        ? DisplayName
        : string.Format(LocalizedText.Get("settings.system_style.unavailable_format", "{0} (not installed)"), DisplayName);
}

public sealed record ThemePaletteChoice(string Id, string Name, bool IsCustom);
public sealed record ThemePalettePreview(IBrush Background, IBrush Surface, IBrush Accent, IBrush Success, IBrush Danger, string AccentValue);
