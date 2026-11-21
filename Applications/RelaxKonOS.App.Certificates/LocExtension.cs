using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace RelaxKonOS.App.Certificates;

public sealed class LocExtension(string key) : MarkupExtension
{
    public static IValueConverter Converter { get; } = new LabelConverter();

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(CertificateManagerViewModel.CurrentLanguage))
        {
            Converter = Converter,
            ConverterParameter = key,
        };

    private sealed class LabelConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is string language && parameter is string resource
                ? CertificateLocalization.Resolve(language, resource)
                : null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            BindingOperations.DoNothing;
    }
}
