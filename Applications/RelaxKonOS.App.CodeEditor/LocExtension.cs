using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace RelaxKonOS.App.CodeEditor;

/// <summary>Looks up editor labels through the view model, including in detached window headers.</summary>
public sealed class LocExtension(string key) : MarkupExtension
{
    private static readonly LabelConverter Converter = new();

    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding(nameof(CodeEditorViewModel.Labels))
    {
        Converter = Converter,
        ConverterParameter = key,
    };

    private sealed class LabelConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is CodeEditorLabels labels && parameter is string label ? labels[label] : null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            BindingOperations.DoNothing;
    }
}

public sealed class CodeEditorLabels(Func<string, string> localize)
{
    public string this[string key] => localize(key);
}
