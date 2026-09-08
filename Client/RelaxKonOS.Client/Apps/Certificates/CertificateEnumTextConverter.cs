using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Certificates;

namespace RelaxKonOS.Client.Apps.Certificates;

/// <summary>Localizes certificate protocol enum values displayed by the manager.</summary>
public sealed class CertificateEnumTextConverter : IValueConverter
{
    public static readonly CertificateEnumTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        CertificateStatus status => LocalizedText.Get($"certificates.status_value.{status.ToString().ToLowerInvariant()}", LocalizedText.Get("certificates.status_value.unknown")),
        CertificateChallengeType.DirectHttp01 => LocalizedText.Get("certificates.challenge.direct_http01"),
        CertificateChallengeType.WebRootHttp01 => LocalizedText.Get("certificates.challenge.webroot_http01"),
        CertificateChallengeType.Dns01 => LocalizedText.Get("certificates.challenge.dns01"),
        CertificateKind.Acme => LocalizedText.Get("certificates.kind.acme"),
        CertificateKind.SelfSigned => LocalizedText.Get("certificates.kind.self_signed"),
        _ => LocalizedText.Get("certificates.status_value.unknown"),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
