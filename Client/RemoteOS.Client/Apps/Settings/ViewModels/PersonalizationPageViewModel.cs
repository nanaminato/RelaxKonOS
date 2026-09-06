using Client.Services;
using Client.Localization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Desktop;
using RemoteOS.Protocol.Workspace;
using RemoteOS.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace Client.Apps.Settings.ViewModels;

/// <summary>「个性化」页：壁纸预设选择 + 主题（Light/Dark/System）。
/// 透传读写 <see cref="ShellSettings"/>，改动即时反映到桌面外壳（壁纸 / 任务栏底色）并触发保存。</summary>
public sealed partial class PersonalizationPageViewModel : SettingsPageViewModel
{
    private readonly IShellCatalog _shellCatalog;
    private readonly LocalizationService _localization;
    private IReadOnlyList<ShellChoice> _shellChoices = [];

    public PersonalizationPageViewModel(ShellSettings settings, Action? save, IShellCatalog? shellCatalog = null) : base(settings, save)
    {
        _localization = Client.App.Services.GetRequiredService<LocalizationService>();
        _shellCatalog = shellCatalog ?? Client.App.Services.GetRequiredService<IShellCatalog>();
        RefreshShellChoices();
        _shellCatalog.Changed += (_, _) => RefreshShellChoices();
        _localization.LanguageChanged += (_, _) => RefreshShellChoices();
        // Theme 变化（含外部 Apply 加载）时刷新三个 RadioButton 绑定。
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Theme))
            {
                OnPropertyChanged(nameof(IsLightTheme));
                OnPropertyChanged(nameof(IsDarkTheme));
                OnPropertyChanged(nameof(IsSystemTheme));
                OnPropertyChanged(nameof(PalettePreview));
            }
            else if (e.PropertyName == nameof(ShellSettings.ThemePreferences))
            {
                OnPropertyChanged(nameof(PaletteId));
                AccentInput = Settings.ThemePreferences.AccentOverride ?? string.Empty;
                OnPropertyChanged(nameof(PaletteChoices));
                OnPropertyChanged(nameof(PalettePreview));
                OnPropertyChanged(nameof(SelectedCustomPalette));
                OnPropertyChanged(nameof(HasSelectedCustomPalette));
                OnPropertyChanged(nameof(HasAccentOverride));
            }
            else if (e.PropertyName == nameof(ShellSettings.SelectedShellId))
            {
                // Preferences may arrive after Settings is already open. Keep the ComboBox in
                // sync without treating that inbound update as another user selection.
                OnPropertyChanged(nameof(SelectedShellId));
            }
        };
        _accentInput = Settings.ThemePreferences.AccentOverride ?? string.Empty;
    }

    public override string Glyph => "🎨";
    public override string DisplayNameKey => "settings.page.personalization";
    public override string DisplayName => "Personalization";

    public IReadOnlyList<Client.Services.WallpaperOption> Wallpapers => Settings.Wallpapers;
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

            var id = ShellApi.NormalizeId(value);
            if (!_shellCatalog.TryGet(id, out var shell) || !shell.IsAvailable) return;
            if (Settings.SelectedShellId == id) return;
            Settings.SelectedShellId = id;
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
        "remoteos.windows-like" => T("settings.shell.windows_like", "Windows-style desktop"),
        "remoteos.macos-like" => T("settings.shell.macos_like", "macOS-style desktop"),
        "remoteos.ubuntu-like" => T("settings.shell.ubuntu_like", "Ubuntu-style desktop"),
        _ => fallback,
    };

    /// <summary>Supplied by the Avalonia page so the VM never accesses a TopLevel or filesystem picker.</summary>

    /// <summary>由 SettingsApp 提供本机文件选择器；VM 不直接依赖 Avalonia TopLevel。</summary>
    public Func<Task>? RequestCustomWallpaperAsync { get; set; }
    public Func<Task>? RequestThemeImportAsync { get; set; }
    public Func<ThemePaletteDto, Task>? RequestThemeExportAsync { get; set; }
    public Func<ThemePaletteDto, Task<bool>>? RequestThemeDeletionConfirmationAsync { get; set; }

    public int WallpaperIndex
    {
        get => Settings.WallpaperIndex;
        set { Settings.WallpaperIndex = value; Save(); }
    }

    public ThemeKind Theme
    {
        get => Settings.Theme;
        set { Settings.Theme = value; Save(); }
    }

    public bool IsLightTheme { get => Theme == ThemeKind.Light; set { if (value) Theme = ThemeKind.Light; } }
    public bool IsDarkTheme { get => Theme == ThemeKind.Dark; set { if (value) Theme = ThemeKind.Dark; } }
    public bool IsSystemTheme { get => Theme == ThemeKind.System; set { if (value) Theme = ThemeKind.System; } }

    /// <summary>Built-ins deliberately share the same semantic token contract in every RemoteOS app.</summary>
    public IReadOnlyList<ThemePaletteChoice> PaletteChoices =>
    [
        new("builtin:remoteos-blue", "RemoteOS Blue", false),
        new("builtin:nord", "Nord", false),
        new("builtin:catppuccin", "Catppuccin", false),
        .. (Settings.ThemePreferences.CustomPalettes ?? [])
            .Select(palette => new ThemePaletteChoice("custom:" + palette.Id, palette.Name, true)),
    ];

    public string PaletteId
    {
        get => Settings.ThemePreferences.PaletteId;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == Settings.ThemePreferences.PaletteId) return;
            UpdateThemePreferences(value, Settings.ThemePreferences.AccentOverride);
        }
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
            if (color == Settings.ThemePreferences.AccentOverride) return;
            UpdateThemePreferences(Settings.ThemePreferences.PaletteId, color);
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
            var colors = ThemePaletteDefaults.Resolve(Settings.ThemePreferences, dark);
            return new ThemePalettePreview(
                Brush(colors["AppBackground"]), Brush(colors["Surface"]), Brush(colors["Accent"]),
                Brush(colors["Success"]), Brush(colors["Danger"]), colors["Accent"]);
        }
    }

    public ThemePaletteDto? SelectedCustomPalette => Settings.ThemePreferences.PaletteId.StartsWith("custom:", StringComparison.Ordinal)
        ? Settings.ThemePreferences.CustomPalettes?.FirstOrDefault(p => p.Id == Settings.ThemePreferences.PaletteId["custom:".Length..])
        : null;

    public bool HasSelectedCustomPalette => SelectedCustomPalette is not null;

    private void UpdateThemePreferences(string paletteId, string? accent)
    {
        var current = Settings.ThemePreferences;
        Settings.ThemePreferences = new ThemePreferencesDto
        {
            StyleId = "remoteos", PaletteId = paletteId, AccentOverride = accent,
            CustomPalettes = current.CustomPalettes ?? [],
        };
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
            error = T("settings.theme_import.invalid", "This file does not contain a valid RemoteOS theme.");
            return false;
        }

        var existing = Settings.ThemePreferences.CustomPalettes ?? [];
        if (existing.Count >= 20)
        {
            error = T("settings.theme_import.limit", "You can keep up to 20 custom themes.");
            return false;
        }

        if (!ThemePaletteImport.TryNormalize(source, existing.Select(p => p.Id), Settings.ThemePreferences.AccentOverride,
                out var imported, out var importError))
        {
            error = importError == ThemePaletteImportError.Inaccessible
                ? T("settings.theme_import.inaccessible", "This theme does not meet the contrast requirements.")
                : T("settings.theme_import.invalid", "This file does not contain a valid RemoteOS theme.");
            return false;
        }
        var candidate = new ThemePreferencesDto
        {
            StyleId = "remoteos", PaletteId = "custom:" + imported!.Id,
            AccentOverride = Settings.ThemePreferences.AccentOverride,
            CustomPalettes = [.. existing, imported],
        };
        Settings.ThemePreferences = candidate;
        Save();
        return true;
    }

    public void DeleteSelectedCustomPalette()
    {
        var selected = SelectedCustomPalette;
        if (selected is null) return;
        var remaining = Settings.ThemePreferences.CustomPalettes?.Where(p => p.Id != selected.Id).ToList() ?? [];
        Settings.ThemePreferences = new ThemePreferencesDto
        {
            StyleId = "remoteos", PaletteId = ThemePreferencesDto.DefaultPaletteId,
            AccentOverride = Settings.ThemePreferences.AccentOverride, CustomPalettes = remaining,
        };
        Save();
    }

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

public sealed record ThemePaletteChoice(string Id, string Name, bool IsCustom);
public sealed record ThemePalettePreview(IBrush Background, IBrush Surface, IBrush Accent, IBrush Success, IBrush Danger, string AccentValue);
