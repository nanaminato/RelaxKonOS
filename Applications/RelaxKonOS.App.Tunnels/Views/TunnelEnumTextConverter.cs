using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.App.Tunnels.Views;

/// <summary>Localizes tunnel connection-state values used in data grids and status text.</summary>
internal sealed class TunnelEnumTextConverter(Func<string, string> localize) : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is TunnelConnectionState state
        ? localize($"tunnels.connection_state.{state}")
        : localize("tunnels.connection_state.Unknown");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
