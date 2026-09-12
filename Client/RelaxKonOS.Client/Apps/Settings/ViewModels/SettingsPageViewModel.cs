using RelaxKonOS.Client.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>设置页 VM 基类。每页持有 <see cref="Settings"/>（实时外壳绑定源）与一个保存回调（由根
/// <see cref="SettingsViewModel"/> 提供的防抖保存）。透传属性 get/set 直接读写 <see cref="Settings"/>，
/// 外部加载（<c>PreferencesSync</c>）改变 Settings 时通过订阅自动重新通知视图，避免脏数据。</summary>
public abstract class SettingsPageViewModel : ObservableObject
{
    protected readonly ShellSettings Settings;
    private readonly Action? _save;

    protected SettingsPageViewModel(ShellSettings settings, Action? save)
    {
        Settings = settings;
        _save = save;
        // 透传：Settings 属性变化时在本 VM 上重发同名通知，视图即刻刷新（含外部 Apply 加载场景）。
        settings.PropertyChanged += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.PropertyName))
                OnPropertyChanged(e.PropertyName);
            if (e.PropertyName == nameof(ShellSettings.Language))
                OnPropertyChanged(nameof(LocalizedDisplayName));
        };

        App.Services.GetRequiredService<LocalizationService>().LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    public abstract string Route { get; }

    // Shared monochrome geometry uses the navigation foreground in every theme.
    public Avalonia.Media.Geometry Icon => Avalonia.Media.Geometry.Parse(Route switch
    {
        "system" => "M2,3 L22,3 22,17 2,17 Z M8,21 L16,21 M12,17 L12,21",
        "personalization" => "M4,3 L20,3 20,15 4,15 Z M8,15 L8,21 16,21 16,15 M4,8 L20,8",
        "time-language" => "M12,2 A10,10 0 1 1 11.99,2 M12,5 L12,12 17,15",
        "network" => "M12,2 A10,10 0 1 1 11.99,2 M2,12 L22,12 M12,2 C5,8 5,16 12,22 C19,16 19,8 12,2",
        "apps" => "M3,3 L10,3 10,10 3,10 Z M14,3 L21,3 21,10 14,10 Z M3,14 L10,14 10,21 3,21 Z M14,14 L21,14 21,21 14,21 Z",
        "image-mirrors" => "M3,4 L21,4 21,10 3,10 Z M3,14 L21,14 21,20 3,20 Z M6,7 L8,7 M6,17 L8,17",
        "default-apps" => "M9,15 L15,9 M7,16 L5,18 A4,4 0 0 1 1,14 L7,8 A4,4 0 0 1 13,8 M11,16 A4,4 0 0 0 17,16 L23,10 A4,4 0 0 0 19,6 L17,8",
        _ => "M8,5 L2,12 8,19 M16,5 L22,12 16,19 M14,3 L10,21"
    });

    /// <summary>Stable resource key for the category name.</summary>
    public abstract string DisplayNameKey { get; }

    /// <summary>English baseline shown if a language pack does not contain <see cref="DisplayNameKey"/>.</summary>
    public abstract string DisplayName { get; }

    /// <summary>Localized category name used by the Settings navigation and page headers.</summary>
    public string LocalizedDisplayName => App.Services.GetRequiredService<LocalizationService>().Get(DisplayNameKey, DisplayName);

    protected string T(string key, string englishFallback) =>
        App.Services.GetRequiredService<LocalizationService>().Get(key, englishFallback);

    /// <summary>触发根 VM 的防抖保存。仅用户编辑路径调用。</summary>
    protected void Save() => _save?.Invoke();
}
