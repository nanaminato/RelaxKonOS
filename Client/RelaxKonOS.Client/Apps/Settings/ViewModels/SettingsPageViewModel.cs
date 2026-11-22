using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.App.Settings;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>设置页 VM 基类。每页持有 <see cref="Settings"/>（实时外壳绑定源）与一个保存回调（由根
/// <see cref="SettingsViewModel"/> 提供的防抖保存）。透传属性 get/set 直接读写 <see cref="Settings"/>，
/// 外部加载（<c>PreferencesSync</c>）改变 Settings 时通过订阅自动重新通知视图，避免脏数据。</summary>
public abstract class SettingsPageViewModel : SettingsPageModel, IDisposable
{
    protected readonly ShellSettings Settings;
    private readonly Action? _save;
    private readonly LocalizationService _localization;
    protected bool IsDisposed { get; private set; }

    protected SettingsPageViewModel(ShellSettings settings, Action? save)
        : this(settings, save, App.Services.GetRequiredService<LocalizationService>()) { }

    private SettingsPageViewModel(ShellSettings settings, Action? save, LocalizationService localization)
        : base(localization.Get)
    {
        Settings = settings;
        _save = save;
        // 透传：Settings 属性变化时在本 VM 上重发同名通知，视图即刻刷新（含外部 Apply 加载场景）。
        _localization = localization;
        settings.PropertyChanged += OnSettingsChanged;
        _localization.LanguageChanged += OnLanguageChanged;
    }

    protected virtual void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName)) OnPropertyChanged(e.PropertyName);
        if (e.PropertyName == nameof(ShellSettings.Language)) OnPropertyChanged(nameof(LocalizedDisplayName));
    }

    protected virtual void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs e) => OnPropertyChanged(string.Empty);

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Settings.PropertyChanged -= OnSettingsChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
        DisposeCore();
    }

    protected virtual void DisposeCore() { }

    /// <summary>
    /// Captures a resource key for a field that must follow the display language. Prefer this over
    /// <see cref="T"/> when the value is stored rather than rendered immediately.
    /// </summary>
    protected static LocalizedStatus Ref(string key) => LocalizedText.Ref(key);

    /// <summary>Captures a resource key with an English source fallback and optional format arguments.</summary>
    protected static LocalizedStatus Ref(string key, string englishFallback, params object?[] arguments) =>
        LocalizedText.Ref(key, englishFallback, arguments);

    /// <summary>Captures a resource key with a single format argument for a stored value.</summary>
    protected static LocalizedStatus Ref(string key, object? argument) => LocalizedText.Ref(key, argument);

    /// <summary>Captures a resource key with two format arguments for a stored value.</summary>
    protected static LocalizedStatus Ref(string key, object? first, object? second) =>
        LocalizedText.Ref(key, first, second);

    /// <summary>Captures a resource key with three format arguments for a stored value.</summary>
    protected static LocalizedStatus Ref(string key, object? first, object? second, object? third) =>
        LocalizedText.Ref(key, first, second, third);

    /// <summary>Captures a resource key with an arbitrary argument list for a stored value.</summary>
    protected static LocalizedStatus Ref(string key, params object?[] arguments) => LocalizedText.Ref(key, arguments);

    /// <summary>触发根 VM 的防抖保存。仅用户编辑路径调用。</summary>
    protected void Save() => _save?.Invoke();
}
