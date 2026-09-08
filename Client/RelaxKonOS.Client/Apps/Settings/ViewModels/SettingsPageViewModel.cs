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

    /// <summary>分类图标（emoji）。</summary>
    public abstract string Glyph { get; }

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
