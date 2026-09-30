using System.Globalization;
using System.Net;
using RelaxKonOS.Protocol.Certificates;

namespace RelaxKonOS.Server.Certificate;

internal static class CertificateUsagePolicy
{
    internal static string? Problem(StoredCertificate? certificate, DateTimeOffset now)
    {
        if (certificate is null) return "certificate.not_found";
        if (certificate.Status == CertificateStatus.Revoked) return "certificate.revoked";
        if (certificate.Status is not (CertificateStatus.Issued or CertificateStatus.Active)
            || certificate.NotBefore > now || certificate.NotAfter <= now) return "certificate.not_usable";
        return null;
    }

    internal static string? NormalizeName(string? supplied)
    {
        if (string.IsNullOrWhiteSpace(supplied)) return null;
        var value = supplied.Trim().TrimEnd('.');
        if (IPAddress.TryParse(value, out var address)) return address.ToString().ToLowerInvariant();
        var wildcard = value.StartsWith("*.", StringComparison.Ordinal);
        try
        {
            var ascii = new IdnMapping().GetAscii(wildcard ? value[2..] : value).ToLowerInvariant();
            if (Uri.CheckHostName(ascii) != UriHostNameType.Dns) return null;
            return (wildcard ? "*." : "") + ascii;
        }
        catch (ArgumentException) { return null; }
    }

    internal static bool Covers(IEnumerable<string> names, string target)
    {
        var host = NormalizeName(target);
        if (host is null) return false;
        foreach (var name in names)
        {
            var san = NormalizeName(name);
            if (san is null) continue;
            if (san == host) return true;
            if (!host.StartsWith("*.", StringComparison.Ordinal) && !IPAddress.TryParse(host, out _)
                && san.StartsWith("*.", StringComparison.Ordinal) && host.IndexOf('.') is var dot && dot > 0
                && host[(dot + 1)..] == san[2..]) return true;
        }
        return false;
    }
}
