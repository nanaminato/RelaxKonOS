using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RelaxKonOS.Protocol.ProxyServer;

internal sealed class ProxyFullServerTrafficProbe : IDisposable
{
    private const string ServerName = "proxy-fixture.example";
    private readonly TcpListener origin = new(IPAddress.Parse("93.184.216.34"), 0);
    private readonly RSA key = RSA.Create(2048);
    private readonly X509Certificate2 certificate;
    private int inboundPort;
    private readonly List<TcpClient> connections = [];

    internal ProxyFullServerTrafficProbe()
    {
        var request = new CertificateRequest($"CN={ServerName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(ServerName);
        request.CertificateExtensions.Add(san.Build());
        certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        origin.Start();
    }

    internal ProxyServerMaterial CreateMaterial(ProxyServerMaterial material)
    {
        var inbound = material.Configuration.Inbounds.Single();
        inboundPort = inbound.ListenPort;
        return material with
        {
            Configuration = material.Configuration with
            {
                Inbounds = [inbound with { Protocol = ProxyServerProtocol.VlessTls, CertificateId = Guid.NewGuid(), ServerName = ServerName }]
            },
            Keys = [],
            Certificates = [new(inbound.Id, certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), "fixture")]
        };
    }

    internal async Task<SslStream> ForwardAsync(string credential, CancellationToken ct)
    {
        var stream = await ConnectAsync(ct);
        try
        {
            await SendAsync(stream, credential, ct);
            var accepted = await origin.AcceptTcpClientAsync(ct);
            connections.Add(accepted);
            var data = new byte[1];
            await accepted.GetStream().ReadExactlyAsync(data, ct);
            if (data[0] != 42) throw new InvalidOperationException("Authorized traffic did not reach the isolated origin.");
            await accepted.GetStream().WriteAsync(data, ct);
            var response = new byte[3];
            await stream.ReadExactlyAsync(response, ct);
            if (!response.AsSpan().SequenceEqual(new byte[] { 0, 0, 42 }))
                throw new InvalidOperationException("Authorized traffic did not return through sing-box.");
            return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    internal async Task DeniedAsync(string credential, CancellationToken ct)
    {
        using var stream = await ConnectAsync(ct);
        await SendAsync(stream, credential, ct);
        await ClosedAsync(stream, ct);
        if (origin.Pending()) throw new InvalidOperationException("An expired credential reached the isolated origin.");
    }

    internal static async Task ClosedAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            if (await stream.ReadAsync(new byte[1], ct) != 0)
                throw new InvalidOperationException("A revoked connection forwarded another byte.");
        }
        catch (IOException) when (!ct.IsCancellationRequested) { }
    }

    private async Task<SslStream> ConnectAsync(CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try { await socket.ConnectAsync(IPAddress.Loopback, inboundPort, ct); }
        catch { socket.Dispose(); throw; }
        var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var tls = new SslStream(new NetworkStream(socket, ownsSocket: true), false, (_, peer, _, errors) =>
            peer?.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint
            && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0);
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = ServerName }, ct);
            return tls;
        }
        catch { await tls.DisposeAsync(); throw; }
    }

    private async Task SendAsync(Stream stream, string credential, CancellationToken ct)
    {
        var port = ((IPEndPoint)origin.LocalEndpoint).Port;
        byte[] packet = [0, .. Guid.Parse(credential).ToByteArray(bigEndian: true), 0, 1,
            (byte)(port >> 8), (byte)port, 1, 93, 184, 216, 34, 42];
        await stream.WriteAsync(packet, ct);
    }

    public void Dispose()
    {
        foreach (var connection in connections) connection.Dispose();
        origin.Stop();
        certificate.Dispose();
        key.Dispose();
    }
}
