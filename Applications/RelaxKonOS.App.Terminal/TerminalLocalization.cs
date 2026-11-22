using System.Text.Json;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Terminal;

internal static class TerminalLocalization
{
    private static readonly Lazy<Dictionary<string, string>> English = new(() => Load("en-US"));
    private static readonly Lazy<Dictionary<string, string>> Chinese = new(() => Load("zh-CN"));
    private static readonly Lazy<Dictionary<string, string>> Japanese = new(() => Load("ja-JP"));

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
        var resourceName = $"Terminal.Localization.{culture}.json";
        using var stream = typeof(TerminalLocalization).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"Missing Terminal localization resource '{resourceName}'.");
        return JsonSerializer.Deserialize<LanguageFile>(stream)!.Strings;
    }

    private sealed record LanguageFile(Dictionary<string, string> Strings);
}
