using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.App.Settings;

/// <summary>Top-level editor for URI scheme and file-extension default application mappings.</summary>
public sealed partial class DefaultAppsPageContent(Action save, Func<string, string> labels) : ObservableObject
{
    public Func<string, string> Labels { get; } = labels;

    public void RefreshLanguage() => OnPropertyChanged(nameof(Labels));

    public ObservableCollection<AppOption> AvailableApps { get; } = new();
    public ObservableCollection<string> AvailableSchemes { get; } = new();
    public ObservableCollection<DefaultAppMappingViewModel> Mappings { get; } = new();

    public void SetApplications(IReadOnlyList<AppOption> apps)
    {
        Replace(AvailableApps, apps);
        Replace(AvailableSchemes, new[] { "http", "https", "mailto", "ftp" }
            .Concat(apps.SelectMany(app => app.SupportedUriSchemes))
            .Concat(apps.SelectMany(app => app.SupportedFileExtensions))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(scheme => scheme.StartsWith('.') ? 1 : 0)
            .ThenBy(scheme => scheme, StringComparer.OrdinalIgnoreCase));
        foreach (var mapping in Mappings) mapping.NotifyAvailableAppsChanged();
    }

    [RelayCommand]
    private void ResetMappings() { Mappings.Clear(); save(); }

    [RelayCommand]
    private void AddMapping()
    {
        var preset = AvailableSchemes.FirstOrDefault(scheme => Mappings.All(mapping => !string.Equals(mapping.Scheme, scheme, StringComparison.OrdinalIgnoreCase)))
            ?? AvailableSchemes.FirstOrDefault() ?? "http";
        var defaultApp = AvailableApps.FirstOrDefault()?.Id ?? "relaxkonos.browser";
        Mappings.Add(new DefaultAppMappingViewModel(preset, defaultApp, AvailableApps, save, RemoveMapping));
        save();
    }

    public void SetMappings(IEnumerable<(string Scheme, string AppId)>? mappings)
    {
        Mappings.Clear();
        if (mappings is null) return;
        foreach (var mapping in mappings)
        {
            if (AvailableSchemes.All(scheme => !string.Equals(scheme, mapping.Scheme, StringComparison.OrdinalIgnoreCase)))
                AvailableSchemes.Add(mapping.Scheme);
            Mappings.Add(new DefaultAppMappingViewModel(mapping.Scheme, mapping.AppId, AvailableApps, save, RemoveMapping));
        }
    }

    private void RemoveMapping(DefaultAppMappingViewModel mapping)
    {
        Mappings.Remove(mapping);
        save();
    }

    private static void Replace<T>(ObservableCollection<T> destination, IEnumerable<T> source)
    {
        destination.Clear();
        foreach (var item in source) destination.Add(item);
    }
}

public sealed partial class DefaultAppMappingViewModel : ObservableObject
{
    private readonly Action? _save;
    private readonly Action<DefaultAppMappingViewModel>? _remove;

    public DefaultAppMappingViewModel(string scheme, string appId, IReadOnlyList<AppOption> availableApps, Action? save, Action<DefaultAppMappingViewModel>? remove)
    {
        _scheme = scheme;
        _appId = appId;
        AvailableApps = availableApps;
        _save = save;
        _remove = remove;
    }

    public IReadOnlyList<AppOption> AvailableApps { get; }
    [ObservableProperty] private string _scheme;
    [ObservableProperty] private string _appId;

    public AppOption? SelectedApp
    {
        get => AvailableApps.FirstOrDefault(app => string.Equals(app.Id, AppId, StringComparison.Ordinal));
        set { if (value is not null) AppId = value.Id; }
    }

    public IReadOnlyList<AppOption> CompatibleApps
    {
        get
        {
            if (!Scheme.StartsWith('.'))
                return AvailableApps.Where(app => app.SupportedUriSchemes.Contains(Scheme, StringComparer.OrdinalIgnoreCase)
                    || new[] { "http", "https", "mailto", "ftp" }.Contains(Scheme, StringComparer.OrdinalIgnoreCase)).ToArray();

            var anyAppDeclaresExtension = AvailableApps.Any(app =>
                app.SupportedFileExtensions.Contains(Scheme, StringComparer.OrdinalIgnoreCase));
            // 已知扩展名：仅列出显式声明该扩展名的应用（Manifest 权威性）。
            if (anyAppDeclaresExtension)
                return AvailableApps.Where(app =>
                    app.SupportedFileExtensions.Contains(Scheme, StringComparer.OrdinalIgnoreCase)).ToArray();

            // 未知扩展名（用户自由输入的 .enabled / .mytext 等）：列出所有支持文本文件兜底的应用
            // 以及其他任何带 IFileOpenApplication 特征（SupportedFileExtensions 非空或 SupportsTextFiles）
            // 的应用，让用户可以手动绑定。
            return AvailableApps.Where(app =>
                app.SupportedFileExtensions.Count > 0).ToArray();
        }
    }

    [RelayCommand]
    private void Remove() => _remove?.Invoke(this);

    partial void OnSchemeChanged(string value)
    {
        _save?.Invoke();
        OnPropertyChanged(nameof(CompatibleApps));
        OnPropertyChanged(nameof(SelectedApp));
    }

    partial void OnAppIdChanged(string value)
    {
        _save?.Invoke();
        OnPropertyChanged(nameof(SelectedApp));
    }

    public void NotifyAvailableAppsChanged()
    {
        OnPropertyChanged(nameof(CompatibleApps));
        OnPropertyChanged(nameof(SelectedApp));
    }
}

public sealed record AppOption(
    string Id,
    string DisplayName,
    IReadOnlyList<string> SupportedFileExtensions,
    IReadOnlyList<string> SupportedUriSchemes);
