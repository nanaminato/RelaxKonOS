using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia;
using RelaxKonOS.Protocol.Certificates;

namespace RelaxKonOS.App.Certificates;

/// <summary>Localizes certificate protocol enum values displayed by the manager.</summary>
public sealed class CertificateEnumTextConverter : IMultiValueConverter
{
    public static readonly CertificateEnumTextConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [var value, string language] || value == AvaloniaProperty.UnsetValue)
            return BindingOperations.DoNothing;
        string Text(string key, string? fallback = null) => CertificateLocalization.Resolve(language, key, fallback);
        return value switch
        {
            CertificateStatus status => Text($"certificates.status_value.{status.ToString().ToLowerInvariant()}", Text("certificates.status_value.unknown")),
            CertificateChallengeType.DirectHttp01 => Text("certificates.challenge.direct_http01"),
            CertificateChallengeType.WebRootHttp01 => Text("certificates.challenge.webroot_http01"),
            CertificateChallengeType.Dns01 => Text("certificates.challenge.dns01"),
            CertificateKind.Acme => Text("certificates.kind.acme"),
            CertificateKind.SelfSigned => Text("certificates.kind.self_signed"),
            _ => Text("certificates.status_value.unknown"),
        };
    }
}
