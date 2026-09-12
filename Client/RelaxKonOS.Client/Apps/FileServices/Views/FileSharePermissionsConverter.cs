using System.Globalization;
using Avalonia.Data.Converters;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.FileServices;

namespace RelaxKonOS.Client.Apps.FileServices.Views;

public sealed class FileSharePermissionsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IReadOnlyList<FileSharePermissionDto> rules
            ? string.Join(", ", rules.Select(rule => rule.Principal + ": " + LocalizedText.Get("file_services.access." + rule.Access, rule.Access.ToString())))
            : "—";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
