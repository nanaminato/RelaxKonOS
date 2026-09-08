using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.WebServers;

/// <summary>Localizes protocol enum values that are displayed directly in web-server views.</summary>
public sealed class WebServerEnumTextConverter : IValueConverter
{
    public static readonly WebServerEnumTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var category = parameter as string;
        var name = value?.ToString()?.ToLowerInvariant();
        return category is null || string.IsNullOrEmpty(name)
            ? LocalizedText.Get("webservers.enum.unknown")
            : LocalizedText.Get($"webservers.enum.{category}.{name}", LocalizedText.Get("webservers.enum.unknown"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
