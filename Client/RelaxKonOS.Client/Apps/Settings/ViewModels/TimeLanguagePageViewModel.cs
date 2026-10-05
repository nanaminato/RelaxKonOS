using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Services.Diagnostics;
using System.Globalization;
using RelaxKonOS.Client.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>Workspace display formats and a separate, explicitly targeted remote host time editor.</summary>
public sealed partial class TimeLanguagePageViewModel : SettingsPageViewModel
{
    private readonly LocalizationService _localization;
    private bool _refreshingLanguageOptions;

    public TimeLanguagePageViewModel(ShellSettings settings, LocalizationService localization, Action? save,
        HostTimeEditorViewModel hostTime) : base(settings, save)
    {
        HostTime = hostTime;
        _localization = localization;
        LanguageOptions = BuildLanguageOptions();
    }

    protected override void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnSettingsChanged(sender, e);
        if (e.PropertyName is nameof(TimeFormat) or nameof(Language)) OnPropertyChanged(nameof(TimeSample));
        if (e.PropertyName is nameof(DateFormat) or nameof(Language)) OnPropertyChanged(nameof(DateSample));
        if (e.PropertyName == nameof(Language)) OnPropertyChanged(nameof(SelectedLanguage));
    }

    protected override void OnLanguageChanged(object? sender, RelaxKonOS.AppSDK.SystemLanguageChangedEventArgs e)
    {
        // Let the active TwoWay selection write finish before replacing its ItemsSource.
        // Otherwise the control can replay the previous item or lose the new selection.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (IsDisposed) return;
            LanguageSwitchDiagnostics.Record("selection.options.rebuild", new { requested = Language, effective = _localization.CurrentLanguage });
            _refreshingLanguageOptions = true;
            try
            {
                LanguageOptions = BuildLanguageOptions();
                base.OnLanguageChanged(sender, e);
                OnPropertyChanged(nameof(SelectedLanguage));
            }
            finally { _refreshingLanguageOptions = false; }
        });
    }

    public override string Route => "time-language";
    public override string DisplayNameKey => "settings.page.time_language";
    public override string DisplayName => "Time & language";

    public static IReadOnlyList<string> TimeFormats { get; } = new[] { "24h", "12h" };

    public static IReadOnlyList<string> DateFormats { get; } = new[]
    {
        "yyyy/M/d",
        "yyyy-MM-dd",
        "M/d/yyyy",
        "dddd, M/d",
    };

    public IReadOnlyList<SystemLanguageOption> LanguageOptions { get; private set; }

    public static IReadOnlyList<string> Regions { get; } = new[] { "zh-CN", "en-US", "ja-JP" };

    public string TimeFormat
    {
        get => Settings.TimeFormat;
        set { Settings.TimeFormat = value; Save(); }
    }

    public string DateFormat
    {
        get => Settings.DateFormat;
        set { Settings.DateFormat = value; Save(); }
    }

    public string Language
    {
        get => Settings.Language;
        set
        {
            LanguageSwitchDiagnostics.Record("selection.language", new { previous = Settings.Language, requested = value });
            Settings.Language = value;
            Save();
            LanguageSwitchDiagnostics.Record("selection.completed", new { requested = value, actual = Settings.Language, effective = _localization.CurrentLanguage });
        }
    }

    public SystemLanguageOption? SelectedLanguage
    {
        get => LanguageOptions.FirstOrDefault(option => string.Equals(option.Culture, Language, StringComparison.OrdinalIgnoreCase));
        set
        {
            LanguageSwitchDiagnostics.Record("selection.item", new { requested = value?.Culture, actual = Language });
            if (_refreshingLanguageOptions)
            {
                LanguageSwitchDiagnostics.Record("selection.item.ignored_refresh", new { requested = value?.Culture, actual = Language });
                return;
            }
            if (value is not null && !string.Equals(value.Culture, Language, StringComparison.OrdinalIgnoreCase))
                Language = value.Culture;
        }
    }

    public string Region
    {
        get => Settings.Region;
        set { Settings.Region = value; Save(); }
    }

    public HostTimeEditorViewModel HostTime { get; }
    protected override void DisposeCore() => HostTime.Dispose();

    public string TimeSample => FormatTime(DateTime.Now);
    public string DateSample => FormatDate(DateTime.Now);

    /// <summary>供桌面外壳时钟复用的格式化：按当前语言 culture + 12/24h 制。</summary>
    public string FormatTime(DateTime t)
    {
        var culture = SafeCulture(_localization.CurrentLanguage);
        var fmt = TimeFormat == "12h" ? "h:mm tt" : "HH:mm";
        return t.ToString(fmt, culture);
    }

    /// <summary>供桌面外壳时钟复用的格式化：按当前语言 culture + 日期格式。</summary>
    public string FormatDate(DateTime t)
        => t.ToString(string.IsNullOrWhiteSpace(DateFormat) ? "yyyy/M/d" : DateFormat, SafeCulture(_localization.CurrentLanguage));

    private IReadOnlyList<SystemLanguageOption> BuildLanguageOptions() =>
    [
        new SystemLanguageOption(WorkspacePreferencesDto.LanguageFollowSystem,
            _localization.Get("settings.language.follow_system", "Follow system")),
        .. _localization.AvailableLanguages,
    ];

    [RelayCommand]
    private void ResetTimeFormats()
    {
        Settings.TimeFormat = WorkspacePreferencesDto.Default.TimeFormat;
        Settings.DateFormat = WorkspacePreferencesDto.Default.DateFormat;
        Save();
    }

    private static CultureInfo SafeCulture(string name)
    {
        try { return CultureInfo.GetCultureInfo(name); }
        catch { return CultureInfo.InvariantCulture; }
    }
}
