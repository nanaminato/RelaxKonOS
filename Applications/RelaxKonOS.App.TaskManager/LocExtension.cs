using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace RelaxKonOS.App.TaskManager;

public sealed class LocExtension(string key) : MarkupExtension
{
    private static readonly IValueConverter Converter = new LabelConverter();

    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding("Labels")
    {
        Converter = Converter,
        ConverterParameter = key,
    };

    private sealed class LabelConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is TaskManagerText labels && parameter is string resource ? labels.Get(resource) : null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            BindingOperations.DoNothing;
    }
}
