using System.Text.Json;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Databases;

public static class DatabaseLocalization
{
    private static readonly Lazy<Dictionary<string, string>> English = new(() => Load("en-US"));
    private static readonly Lazy<Dictionary<string, string>> Chinese = new(() => Load("zh-CN"));
    private static readonly Lazy<Dictionary<string, string>> Japanese = new(() => Load("ja-JP"));

    public static string Resolve(ISystemLanguage language, string key, string fallback)
    {
        return Resolve(language.CurrentLanguage, key, fallback);
    }

    internal static string Resolve(string culture, string key, string fallback) =>
        Labels(culture).GetValueOrDefault(key) ?? English.Value.GetValueOrDefault(key) ?? fallback;

    internal static IReadOnlyDictionary<string, string> Labels(string culture) =>
        culture.Split('-', 2)[0].ToLowerInvariant() switch
        {
            "zh" => Chinese.Value,
            "ja" => Japanese.Value,
            _ => English.Value,
        };

    private static Dictionary<string, string> Load(string culture)
    {
        var labels = LoadFragment($"Databases.Localization.{culture}.json");
        foreach (var (key, value) in LoadFragment($"Databases.SharedLocalization.{culture}.json"))
            labels.Add(key, value);
        return labels;
    }

    private static Dictionary<string, string> LoadFragment(string resource)
    {
        using var stream = typeof(DatabaseLocalization).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException($"Missing Databases localization resource '{resource}'.");
        return JsonSerializer.Deserialize<LanguageFile>(stream)!.Strings;
    }

    private sealed record LanguageFile(Dictionary<string, string> Strings);
}
