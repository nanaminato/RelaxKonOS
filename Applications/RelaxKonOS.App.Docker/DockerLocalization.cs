using System.Text.Json;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Docker;

public static class DockerLocalization
{
    public static IEnumerable<string> ResourceNames => typeof(DockerLocalization).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("Docker.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(DockerLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing Docker localization resource '{resourceName}'.");

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
        using var application = Open($"Docker.Localization.{culture}.json");
        using var common = Open($"Docker.PackageLocalization.{culture}.json");
        var labels = JsonSerializer.Deserialize<LanguageFile>(application)!.Strings;
        foreach (var (key, value) in JsonSerializer.Deserialize<LanguageFile>(common)!.Strings)
            labels.Add(key, value);
        return labels;
    }

    private sealed record LanguageFile(Dictionary<string, string> Strings);
}
