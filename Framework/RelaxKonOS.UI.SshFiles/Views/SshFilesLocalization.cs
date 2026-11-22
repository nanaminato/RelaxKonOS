using System.Globalization;
using System.Text.Json;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.UI.SshFiles;

internal static class SshFilesLocalization
{
    private static readonly Lazy<Dictionary<string, string>> English = new(() => Load("en-US"));
    private static readonly Lazy<Dictionary<string, string>> Chinese = new(() => Load("zh-CN"));
    private static readonly Lazy<Dictionary<string, string>> Japanese = new(() => Load("ja-JP"));

    public static string Get(ISystemLanguage? language, string key, string? fallback = null)
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
        using var stream = typeof(SshFilesLocalization).Assembly.GetManifestResourceStream($"SshFiles.Localization.{culture}.json")
            ?? throw new InvalidDataException($"Missing SSH Files translations for {culture}.");
        return JsonSerializer.Deserialize<LanguageFile>(stream)!.Strings;
    }

    private sealed record LanguageFile(Dictionary<string, string> Strings);
}

internal sealed class SshLabels(ISystemLanguage language)
{
    public string Get(string key) => SshFilesLocalization.Get(language, key);
}

public sealed class LocExtension(string key) : MarkupExtension
{
    private static readonly LabelConverter Converter = new();

    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding("Labels")
    {
        Source = ((IRootObjectProvider)serviceProvider.GetService(typeof(IRootObjectProvider))!).RootObject,
        Converter = Converter,
        ConverterParameter = key,
    };

    private sealed class LabelConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is SshLabels labels && parameter is string key ? labels.Get(key) : null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            BindingOperations.DoNothing;
    }
}
