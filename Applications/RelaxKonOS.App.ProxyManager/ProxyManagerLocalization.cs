using System.Text.Json;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.ProxyManager;

public static class ProxyManagerLocalization
{
    private static readonly Lazy<Dictionary<string, string>> English = new(() => Load("en-US"));
    private static readonly Lazy<Dictionary<string, string>> Chinese = new(() => Load("zh-CN"));
    private static readonly Lazy<Dictionary<string, string>> Japanese = new(() => Load("ja-JP"));

    public static string Resolve(ISystemLanguage language, string key, string fallback)
    {
        var labels = language.CurrentLanguage.Split('-', 2)[0].ToLowerInvariant() switch
        {
            "zh" => Chinese.Value,
            "ja" => Japanese.Value,
            _ => English.Value,
        };
        return labels.GetValueOrDefault(key) ?? English.Value.GetValueOrDefault(key) ?? fallback;
    }

    private static Dictionary<string, string> Load(string culture)
    {
        var resource = $"ProxyManager.Localization.{culture}.json";
        using var stream = typeof(ProxyManagerLocalization).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException($"Missing Proxy Manager localization resource '{resource}'.");
        return JsonSerializer.Deserialize<LanguageFile>(stream)!.Strings;
    }

    private sealed record LanguageFile(Dictionary<string, string> Strings);
}
