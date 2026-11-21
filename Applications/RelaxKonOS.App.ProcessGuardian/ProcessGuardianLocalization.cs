using System.Text.Json;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.ProcessGuardian;

public static class ProcessGuardianLocalization
{
    private static readonly Lazy<Dictionary<string, string>> English = new(() => Load("en-US"));
    private static readonly Lazy<Dictionary<string, string>> Chinese = new(() => Load("zh-CN"));
    private static readonly Lazy<Dictionary<string, string>> Japanese = new(() => Load("ja-JP"));

    public static string Resolve(ISystemLanguage language, string key)
    {
        var labels = language.CurrentLanguage.Split('-', 2)[0].ToLowerInvariant() switch
        {
            "zh" => Chinese.Value,
            "ja" => Japanese.Value,
            _ => English.Value,
        };
        return labels.GetValueOrDefault(key) ?? English.Value.GetValueOrDefault(key) ?? key;
    }

    private static Dictionary<string, string> Load(string culture)
    {
        using var application = Open($"ProcessGuardian.Localization.{culture}.json");
        using var package = Open($"ProcessGuardian.PackageLocalization.{culture}.json");
        var labels = JsonSerializer.Deserialize<LanguageFile>(application)!.Strings;
        foreach (var (key, value) in JsonSerializer.Deserialize<LanguageFile>(package)!.Strings)
            labels.Add(key, value);
        return labels;
    }

    private sealed record LanguageFile(Dictionary<string, string> Strings);

    public static Stream Open(string resourceName) =>
        typeof(ProcessGuardianLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing ProcessGuardian localization resource '{resourceName}'.");
}
