using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace RelaxKonOS.App.Browser;

public sealed class LocExtension(string key) : MarkupExtension
{
    public static IValueConverter Converter { get; } = new LabelConverter();

    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding(nameof(BrowserLocalizedObject.Text))
    {
        Converter = Converter,
        ConverterParameter = key,
    };

    private sealed class LabelConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is BrowserText text && parameter is string resource ? text.Get(resource) : null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            BindingOperations.DoNothing;
    }
}
