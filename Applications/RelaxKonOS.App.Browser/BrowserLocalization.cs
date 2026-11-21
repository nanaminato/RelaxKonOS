using System.Globalization;
using System.Text.Json;
using Avalonia.Utilities;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Browser;

public static class BrowserLocalization
{
    private static readonly Lazy<Dictionary<string, string>> English = new(() => Load("en-US"));
    private static readonly Lazy<Dictionary<string, string>> Chinese = new(() => Load("zh-CN"));
    private static readonly Lazy<Dictionary<string, string>> Japanese = new(() => Load("ja-JP"));

    public static IEnumerable<string> ResourceNames => typeof(BrowserLocalization).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("Browser.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(BrowserLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing Browser localization resource '{resourceName}'.");

    internal static string Resolve(ISystemLanguage? language, string key)
    {
        var labels = language?.CurrentLanguage.Split('-', 2)[0].ToLowerInvariant() switch
        {
            "zh" => Chinese.Value,
            "ja" => Japanese.Value,
            _ => English.Value,
        };
        return labels.GetValueOrDefault(key) ?? English.Value.GetValueOrDefault(key) ?? key;
    }

    private static Dictionary<string, string> Load(string culture)
    {
        using var stream = Open($"Browser.Localization.{culture}.json");
        return JsonSerializer.Deserialize<LanguageFile>(stream)!.Strings;
    }

    private sealed record LanguageFile(Dictionary<string, string> Strings);
}

public sealed class BrowserText(ISystemLanguage? language)
{
    public string Get(string key) => BrowserLocalization.Resolve(language, key);
    public string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);
    public BrowserLabel Ref(string key, params object?[] args) =>
        new(() => args.Length == 0 ? Get(key) : Format(key, args));
}

public readonly struct BrowserLabel(Func<string> resolve)
{
    public string Resolve() => resolve?.Invoke() ?? string.Empty;
    public override string ToString() => Resolve();
    public static implicit operator string(BrowserLabel value) => value.Resolve();
}

public abstract class BrowserLocalizedObject : ObservableObject
{
    protected BrowserLocalizedObject(ISystemLanguage? language)
    {
        Text = new BrowserText(language);
        if (language is not null)
            WeakEventHandlerManager.Subscribe<ISystemLanguage, SystemLanguageChangedEventArgs, BrowserLocalizedObject>(
                language, nameof(ISystemLanguage.LanguageChanged), OnLanguageChanged);
    }

    public BrowserText Text { get; }

    private void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs args) => OnPropertyChanged(string.Empty);
}
