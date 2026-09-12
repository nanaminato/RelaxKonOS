using System.Globalization;
using RelaxKonOS.Client.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>Workspace display formats and a separate, explicitly targeted remote host time editor.</summary>
public sealed partial class TimeLanguagePageViewModel : SettingsPageViewModel, IDisposable
{
    private readonly LocalizationService _localization;

    public TimeLanguagePageViewModel(ShellSettings settings, LocalizationService localization, Action? save,
        HostTimeEditorViewModel hostTime) : base(settings, save)
    {
        HostTime = hostTime;
        _localization = localization;
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TimeFormat) or nameof(Language))
                OnPropertyChanged(nameof(TimeSample));
            if (e.PropertyName is nameof(DateFormat) or nameof(Language))
                OnPropertyChanged(nameof(DateSample));
            if (e.PropertyName == nameof(Language))
                OnPropertyChanged(nameof(SelectedLanguage));
        };
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

    public IReadOnlyList<SystemLanguageOption> Languages => _localization.AvailableLanguages;

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
        set { Settings.Language = value; Save(); }
    }

    public SystemLanguageOption? SelectedLanguage
    {
        get => Languages.FirstOrDefault(option => string.Equals(option.Culture, Language, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
                Language = value.Culture;
        }
    }

    public string Region
    {
        get => Settings.Region;
        set { Settings.Region = value; Save(); }
    }

    public HostTimeEditorViewModel HostTime { get; }
    public void Dispose() => HostTime.Dispose();

    public string TimeSample => FormatTime(DateTime.Now);
    public string DateSample => FormatDate(DateTime.Now);

    /// <summary>供桌面外壳时钟复用的格式化：按当前语言 culture + 12/24h 制。</summary>
    public string FormatTime(DateTime t)
    {
        var culture = SafeCulture(Language);
        var fmt = TimeFormat == "12h" ? "h:mm tt" : "HH:mm";
        return t.ToString(fmt, culture);
    }

    /// <summary>供桌面外壳时钟复用的格式化：按当前语言 culture + 日期格式。</summary>
    public string FormatDate(DateTime t)
        => t.ToString(string.IsNullOrWhiteSpace(DateFormat) ? "yyyy/M/d" : DateFormat, SafeCulture(Language));

    private static CultureInfo SafeCulture(string name)
    {
        try { return CultureInfo.GetCultureInfo(name); }
        catch { return CultureInfo.InvariantCulture; }
    }
}
