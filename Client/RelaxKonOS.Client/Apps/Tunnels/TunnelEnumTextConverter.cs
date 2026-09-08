using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.Client.Apps.Tunnels;

/// <summary>Localizes tunnel connection-state values used in data grids and status text.</summary>
public sealed class TunnelEnumTextConverter : IValueConverter
{
    public static readonly TunnelEnumTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is TunnelConnectionState state
        ? LocalizedText.Get($"tunnels.connection_state.{state}")
        : LocalizedText.Get("tunnels.connection_state.Unknown");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
