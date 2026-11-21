using System.Text.Json;
using RelaxKonOS.App.CodeEditor;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Git;

public static class GitLocalization
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
        return labels.GetValueOrDefault(key) ?? English.Value.GetValueOrDefault(key)
            ?? CodeEditorLocalization.Resolve(language, key);
    }

    private static Dictionary<string, string> Load(string culture)
    {
        using var resource = Open($"Git.Localization.{culture}.json");
        return JsonSerializer.Deserialize<LanguageFile>(resource)!.Strings;
    }

    private sealed record LanguageFile(Dictionary<string, string> Strings);

    public static IEnumerable<string> ResourceNames => typeof(GitLocalization).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("Git.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(GitLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing Git localization resource '{resourceName}'.");
}
