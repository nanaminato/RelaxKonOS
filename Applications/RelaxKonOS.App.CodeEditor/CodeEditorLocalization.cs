using System.Text.Json;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.CodeEditor;

/// <summary>Resource names for the editor labels shared by the standalone and Git editors.</summary>
public static class CodeEditorLocalization
{
    public static IEnumerable<string> ResourceNames => typeof(CodeEditorLocalization).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("CodeEditor.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(CodeEditorLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing editor localization resource '{resourceName}'.");

    private static readonly Lazy<IReadOnlyDictionary<string, string>> English = new(() => Load("en-US"));
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Chinese = new(() => Load("zh-CN"));
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Japanese = new(() => Load("ja-JP"));

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
        using var application = Open($"CodeEditor.Localization.{culture}.json");
        using var package = Open($"CodeEditor.PackageLocalization.{culture}.json");
        var labels = JsonSerializer.Deserialize<LanguageFile>(application)!.Strings;
        foreach (var (key, value) in JsonSerializer.Deserialize<LanguageFile>(package)!.Strings)
            labels.Add(key, value);
        return labels;
    }

    private sealed record LanguageFile(Dictionary<string, string> Strings);
}
