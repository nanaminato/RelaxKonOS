using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using RelaxKonOS.Protocol.FileServices;

namespace RelaxKonOS.App.FileServices.Views;

public sealed class SharePermissionsBindingExtension : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var root = (StyledElement)((IRootObjectProvider)serviceProvider.GetService(typeof(IRootObjectProvider))!).RootObject;
        return new Binding(nameof(FileShareDto.Permissions)) { Converter = new PermissionsConverter(root) };
    }

    private sealed class PermissionsConverter(StyledElement root) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not IReadOnlyList<FileSharePermissionDto> rules) return "—";
            var model = root.DataContext as FileServicesViewModel;
            return string.Join(", ", rules.Select(rule => rule.Principal + ": " + Label(rule.Access)));

            string Label(FileShareAccess access)
            {
                var key = "file_services.access." + access;
                var label = model?.Labels(key);
                return label is null || label == key ? access.ToString() : label;
            }
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            BindingOperations.DoNothing;
    }
}
