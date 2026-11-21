using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace RelaxKonOS.App.FileServices.Views;

public sealed class LocExtension(string key) : MarkupExtension
{
    private static readonly LabelConverter Converter = new();

    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding($"DataContext.{nameof(FileServicesViewModel.Labels)}")
    {
        Source = ((IRootObjectProvider)serviceProvider.GetService(typeof(IRootObjectProvider))!).RootObject,
        Converter = Converter,
        ConverterParameter = key,
    };

    private sealed class LabelConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is Func<string, string> labels && parameter is string label ? labels(label) : null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            BindingOperations.DoNothing;
    }
}
