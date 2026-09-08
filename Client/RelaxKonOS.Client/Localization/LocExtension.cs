using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using RelaxKonOS.Client.Services;
using Microsoft.Extensions.DependencyInjection;

namespace RelaxKonOS.Client.Localization;

/// <summary>
/// Binds an Avalonia property to a stable localization key. The binding listens to
/// <see cref="LocalizationService.CurrentLanguage"/>, so open views update as soon as
/// the user switches the display language.
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    private static readonly IValueConverter Converter = new LocalizationConverter();

    public LocExtension(string key) => Key = key;

    public string Key { get; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var localization = App.Services.GetRequiredService<LocalizationService>();
        return new Binding(nameof(LocalizationService.CurrentLanguage))
        {
            Source = localization,
            Converter = Converter,
            ConverterParameter = Key
        };
    }

    private sealed class LocalizationConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var key = parameter as string ?? string.Empty;
            // Never expose a resource key to the user. A missing optional translation has a
            // readable English fallback while CI verifies that all shipped keys are present.
            return App.Services.GetRequiredService<LocalizationService>().Get(key, ToEnglishFallback(key));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => BindingOperations.DoNothing;

        private static string ToEnglishFallback(string key) => key switch
        {
            "settings.wallpaper.description" => "Choose a desktop background preset or your own image.",
            "settings.wallpaper.choose_image" => "Browse for an image",
            "settings.wallpaper.sync_hint" => "Images are securely stored in this workspace and sync to your other devices.",
            "settings.theme.description" => "Choose the appearance used throughout RelaxKonOS.",
            "settings.theme.light" => "Light",
            "settings.theme.dark" => "Dark",
            "settings.theme.system" => "Use system setting",
            _ => string.Join(" ", key.Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..].Replace('_', ' ')))
        };
    }
}
