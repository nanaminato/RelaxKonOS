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
            // Never expose a resource key to the user. Missing keys surface a "[missing: <key>]"
            // marker so the gap is visible in screenshots and a future resource-validator test
            // can collect every gap from a single pass.
            return App.Services.GetRequiredService<LocalizationService>().Get(key, $"[missing: {key}]");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => BindingOperations.DoNothing;
    }
}
