using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
namespace RelaxKonOS.Protocol.Databases;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PostgreSqlRemoteCommand(Guid InstanceId, long ExpectedRevision, bool Enabled,
    string? ListenAddress = null, string? ServerName = null, string? Database = null, string? Account = null,
    IReadOnlyList<string>? Sources = null, Guid? CertificateId = null, string? CertificateFingerprint = null);
public sealed record PostgreSqlTlsMaterial(string CertificatePem, string PrivateKeyPem)
{ public override string ToString() => "PostgreSqlTlsMaterial [redacted]"; }
public static class PostgreSqlRemoteValidation
{
    public static bool Address(string? text) => IPAddress.TryParse(text, out var ip) && ip.ToString() == text
        && !IPAddress.IsLoopback(ip) && !ip.IsIPv4MappedToIPv6 && !ip.IsIPv6LinkLocal && !ip.IsIPv6Multicast
        && ip.ScopeIdSafe() && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any)
        && (ip.AddressFamily != AddressFamily.InterNetwork || ip.GetAddressBytes()[0] is > 0 and < 224 && ip.GetAddressBytes()[0] != 169);
    private static bool ScopeIdSafe(this IPAddress ip) => ip.AddressFamily != AddressFamily.InterNetworkV6 || ip.ScopeId == 0;
    public static bool Source(string text)
    {
        var parts = text.Split('/');
        if (parts.Length != 2 || !Address(parts[0]) || !int.TryParse(parts[1], out var bits)) return false;
        var bytes = IPAddress.Parse(parts[0]).GetAddressBytes();
        if (bits < (bytes.Length == 4 ? 8 : 32) || bits > bytes.Length * 8) return false;
        for (var bit = bits; bit < bytes.Length * 8; bit++) if ((bytes[bit / 8] & (1 << (7 - bit % 8))) != 0) return false;
        return true;
    }
    public static bool Valid(PostgreSqlRemoteCommand c) => c.InstanceId != Guid.Empty && c.ExpectedRevision > 0 &&
        (c.Enabled ? Address(c.ListenAddress) && c.ServerName is { Length: > 0 and <= 253 } && Uri.CheckHostName(c.ServerName) == UriHostNameType.Dns
            && PostgreSqlAccountValidation.Name(c.Database) && PostgreSqlAccountValidation.Name(c.Account)
            && c.Sources is { Count: > 0 and <= 16 } && c.Sources.All(Source) && c.Sources.Distinct().Count() == c.Sources.Count
            && c.CertificateId is { } id && id != Guid.Empty && c.CertificateFingerprint is { Length: 64 } fp && fp.All(Uri.IsHexDigit)
            : c.ListenAddress is null && c.ServerName is null && c.Database is null && c.Account is null && c.Sources is null && c.CertificateId is null && c.CertificateFingerprint is null);
}
