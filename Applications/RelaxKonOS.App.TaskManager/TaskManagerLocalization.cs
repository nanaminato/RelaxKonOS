using System.Globalization;
using System.Text.Json;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.TaskManager;

public static class TaskManagerLocalization
{
    private static readonly Lazy<Dictionary<string, string>> English = new(() => Load("en-US"));
    private static readonly Lazy<Dictionary<string, string>> Chinese = new(() => Load("zh-CN"));
    private static readonly Lazy<Dictionary<string, string>> Japanese = new(() => Load("ja-JP"));

    public static IEnumerable<string> ResourceNames => typeof(TaskManagerLocalization).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("TaskManager.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(TaskManagerLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing Task Manager localization resource '{resourceName}'.");

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
        using var stream = Open($"TaskManager.Localization.{culture}.json");
        return JsonSerializer.Deserialize<LanguageFile>(stream)!.Strings;
    }

    private sealed record LanguageFile(Dictionary<string, string> Strings);
}
internal sealed class TaskManagerText(ISystemLanguage language)
{
    public string Get(string key) => TaskManagerLocalization.Resolve(language, key);
    public string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);
    public TaskManagerLabel Ref(string key, params object?[] args) => new(key, () => args.Length == 0 ? Get(key) : Format(key, args));
}

public readonly record struct TaskManagerLabel
{
    internal string? Key { get; }
    private readonly Func<string>? _resolve;

    internal TaskManagerLabel(string? key, Func<string> resolve) => (Key, _resolve) = (key, resolve);

    public static implicit operator TaskManagerLabel(string value) => new(null, () => value);
    public static implicit operator string(TaskManagerLabel value) => value.ToString();
    public override string ToString() => _resolve?.Invoke() ?? string.Empty;
}
