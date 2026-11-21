using System.Globalization;
using System.Text.Json;
using Avalonia.Utilities;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Registry;

public static class RegistryLocalization
{
    private static readonly Lazy<Dictionary<string, string>> English = new(() => Load("en-US"));
    private static readonly Lazy<Dictionary<string, string>> Chinese = new(() => Load("zh-CN"));
    private static readonly Lazy<Dictionary<string, string>> Japanese = new(() => Load("ja-JP"));

    public static IEnumerable<string> ResourceNames => typeof(RegistryLocalization).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("Registry.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(RegistryLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing Registry localization resource '{resourceName}'.");

    internal static string Resolve(ISystemLanguage? language, string key, string? fallback)
    {
        var labels = language?.CurrentLanguage.Split('-', 2)[0].ToLowerInvariant() switch
        {
            "zh" => Chinese.Value,
            "ja" => Japanese.Value,
            _ => English.Value,
        };
        return labels.GetValueOrDefault(key) ?? English.Value.GetValueOrDefault(key) ?? fallback ?? key;
    }

    private static Dictionary<string, string> Load(string culture)
    {
        using var file = Open($"Registry.Localization.{culture}.json");
        return JsonSerializer.Deserialize<LanguageFile>(file)!.Strings;
    }

    private sealed record LanguageFile(Dictionary<string, string> Strings);
}

public sealed class RegistryText(ISystemLanguage? language)
{
    public string Get(string key, string? fallback = null) => RegistryLocalization.Resolve(language, key, fallback);
    public string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);
    public RegistryLabel RefWithFallback(string key, string fallback, params object?[] args) =>
        new(() => args.Length == 0 ? Get(key, fallback) : string.Format(CultureInfo.CurrentCulture, Get(key, fallback), args));
}

public readonly struct RegistryLabel(Func<string> resolve)
{
    public static RegistryLabel Literal(string value) => new(() => value);
    public string Resolve() => resolve?.Invoke() ?? string.Empty;
    public override string ToString() => Resolve();
    public static implicit operator string(RegistryLabel value) => value.Resolve();
}

public abstract class RegistryLocalizedObject : ObservableObject
{
    protected RegistryLocalizedObject(ISystemLanguage? language)
    {
        Language = language;
        if (language is not null)
            WeakEventHandlerManager.Subscribe<ISystemLanguage, SystemLanguageChangedEventArgs, RegistryLocalizedObject>(
                language, nameof(ISystemLanguage.LanguageChanged), OnLanguageChanged);
    }

    public RegistryText Text => new(Language);
    protected ISystemLanguage? Language { get; }

    private void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs args) => OnPropertyChanged(string.Empty);
}
