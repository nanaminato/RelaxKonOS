using System.Text.Json;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Firewall;

public static class FirewallLocalization
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
        using var application = Open($"Firewall.Localization.{culture}.json");
        using var package = Open($"Firewall.PackageLocalization.{culture}.json");
        var labels = JsonSerializer.Deserialize<LanguageFile>(application)!.Strings;
        foreach (var (key, value) in JsonSerializer.Deserialize<LanguageFile>(package)!.Strings)
            labels.Add(key, value);
        return labels;
    }

    private sealed record LanguageFile(Dictionary<string, string> Strings);

    public static IEnumerable<string> ResourceNames => typeof(FirewallLocalization).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("Firewall.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(FirewallLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing Firewall localization resource '{resourceName}'.");
}
