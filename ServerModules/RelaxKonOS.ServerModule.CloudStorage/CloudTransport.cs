using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RelaxKonOS.ServerModule.CloudStorage;

internal static class CloudTransport
{
    internal static HttpClient Create(Uri endpoint, bool allowPrivate = false) => new(
        new OriginHandler(endpoint, new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (context, ct) => ConnectAsync(context.DnsEndPoint, allowPrivate, ct),
        }));

    private sealed class OriginHandler(Uri endpoint, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri is not { } uri || uri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(uri.IdnHost, endpoint.IdnHost, StringComparison.OrdinalIgnoreCase)
                || uri.Port != endpoint.Port || uri.UserInfo.Length != 0)
                throw new HttpRequestException("cloud-storage-origin");
            return base.SendAsync(request, ct);
        }
    }

    private static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, bool allowPrivate, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, ct);
        var local = NetworkInterface.GetAllNetworkInterfaces().SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(item => Normalize(item.Address)).ToHashSet();
        var explicitLocal = allowPrivate && IPAddress.TryParse(endpoint.Host, out var literal)
            && literal.AddressFamily == AddressFamily.InterNetwork && !Allowed(literal, false) ? literal : null;
        foreach (var address in addresses.Where(address => Allowed(address, allowPrivate)
                     && (!local.Contains(Normalize(address)) || Normalize(address).Equals(explicitLocal))))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("cloud-storage-address");
    }

    internal static bool Allowed(IPAddress address, bool allowPrivate)
    {
        address = Normalize(address);
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 16)
            return (bytes[0] & 0xe0) == 0x20
                && !(bytes[0] == 0x20 && (bytes[1] == 2 || bytes[1] == 1 && bytes[2] < 4))
                && !(bytes[0] == 0x20 && bytes[1] == 1 && bytes[2] == 0x0d && bytes[3] == 0xb8);
        if (bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && bytes[1] == 168) return allowPrivate;
        return bytes[0] is > 0 and < 224 and not 127
            && !address.Equals(IPAddress.Parse("168.63.129.16"))
            && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            && !(bytes[0] == 192 && bytes[1] == 0)
            && !(bytes[0] == 198 && bytes[1] is 18 or 19);
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    internal static async Task<XDocument> ReadXmlAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true, DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 8 * 1024 * 1024,
        });
        return await XDocument.LoadAsync(reader, LoadOptions.None, ct);
    }

    internal static long? Size(string? value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var size) ? size : null;

    internal static DateTimeOffset? Date(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    internal static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    internal static string Reference(string path) => Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(path));

    internal static string PathFrom(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return "";
        if (reference.Length > 8192) throw new ArgumentException("reference");
        var path = new UTF8Encoding(false, true).GetString(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(reference));
        if (!ValidPath(path)) throw new ArgumentException("reference");
        return path;
    }

    internal static bool ValidPath(string path) => path.Length == 0 || path.Length <= 4096
        && !path.Any(c => char.IsControl(c) || c is '\\')
        && path.TrimEnd('/').Split('/').All(segment => segment is not ("" or "." or ".."));
}
