using System.Text.Json;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.FileServices;

public static class FileServicesLocalization
{
    private static readonly Lazy<Dictionary<string, string>> English = new(() => Load("en-US"));
    private static readonly Lazy<Dictionary<string, string>> Chinese = new(() => Load("zh-CN"));
    private static readonly Lazy<Dictionary<string, string>> Japanese = new(() => Load("ja-JP"));

    internal static string Resolve(ISystemLanguage language, string key)
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
        using var application = Open($"FileServices.Localization.{culture}.json");
        using var package = Open($"FileServices.PackageLocalization.{culture}.json");
        var labels = JsonSerializer.Deserialize<LanguageFile>(application)!.Strings;
        foreach (var (key, value) in JsonSerializer.Deserialize<LanguageFile>(package)!.Strings)
            labels.Add(key, value);
        return labels;
    }

    private sealed record LanguageFile(Dictionary<string, string> Strings);
    public static IEnumerable<string> ResourceNames => typeof(FileServicesLocalization).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("FileServices.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(FileServicesLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing File Services localization resource '{resourceName}'.");
}
