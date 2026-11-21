using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace RelaxKonOS.App.Tailscale.Views;

public sealed class LocExtension(string key) : MarkupExtension
{
    private static readonly LabelConverter Converter = new();

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        CreateBinding(((IRootObjectProvider)serviceProvider.GetService(typeof(IRootObjectProvider))!).RootObject, key);

    public static Binding CreateBinding(object root, string key) =>
        new($"DataContext.{nameof(TailscaleText.Labels)}")
        {
            Source = root,
            Converter = Converter,
            ConverterParameter = key,
        };

    private sealed class LabelConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is Func<string, string> labels && parameter is string key ? labels(key) : null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            BindingOperations.DoNothing;
    }
}
