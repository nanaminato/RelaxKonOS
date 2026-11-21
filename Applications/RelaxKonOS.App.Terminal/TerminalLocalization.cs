using System.Text.Json;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Terminal;

public static class TerminalLocalization
{
    private static readonly Lazy<Dictionary<string, string>> English = new(() => Load("en-US"));
    private static readonly Lazy<Dictionary<string, string>> Chinese = new(() => Load("zh-CN"));
    private static readonly Lazy<Dictionary<string, string>> Japanese = new(() => Load("ja-JP"));

    public static IEnumerable<string> ResourceNames => typeof(TerminalLocalization).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("Terminal.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(TerminalLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing Terminal localization resource '{resourceName}'.");

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
        using var stream = Open($"Terminal.Localization.{culture}.json");
        return JsonSerializer.Deserialize<LanguageFile>(stream)!.Strings;
    }

    private sealed record LanguageFile(Dictionary<string, string> Strings);
}
