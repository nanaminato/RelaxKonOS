using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace RelaxKonOS.App.ComponentCenter;

public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var root = ((IRootObjectProvider)serviceProvider.GetService(typeof(IRootObjectProvider))!).RootObject;
        return new MultiBinding
        {
            Bindings =
            {
                new Binding("DataContext.Labels") { Source = root },
                new Binding("DataContext.Language.CurrentLanguage") { Source = root },
            },
            Converter = new LabelConverter(key),
        };
    }

    private sealed class LabelConverter(string key) : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
            values[0] is Func<string, string> labels ? labels(key) : null;
    }
}
