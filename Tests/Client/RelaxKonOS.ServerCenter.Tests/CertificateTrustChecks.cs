using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RelaxKonOS.Client.Services.Auth;

static class CertificateTrustChecks
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "relaxkonos-tls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var key = RSA.Create(2048);
            using var certificate = CreateCertificate(key, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
            using var chain = new X509Chain();
            chain.Build(certificate);
            var trust = new ServerCertificateTrust(directory);
            var origin = new Uri("https://localhost:5443");
            Check(!trust.Validate(origin, certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors), "自签名证书在确认前被拒绝");
            var review = trust.GetReview(origin.ToString())!;
            Check(review.CanTrust && review.Fingerprint.Length == 64, "可核对自签名证书的 SHA-256 指纹");
            trust.Trust(review);
            Check(trust.Validate(origin, certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors), "明确确认后仅放行已固定证书");
            Check(new ServerCertificateTrust(directory).Validate(origin, certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors), "信任记录重启后可读取");
            Check(!trust.Validate(new Uri("https://localhost:5444"), certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors), "信任不会扩展到其他端口");
            Check(!trust.Validate(new Uri("https://other-host:5443"), certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors), "信任不会扩展到其他主机");
            Check(!trust.Validate(origin, certificate, chain, SslPolicyErrors.RemoteCertificateNameMismatch), "已固定证书仍拒绝主机名不匹配");
            using var changed = CreateCertificate(key, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(3));
            chain.Build(changed);
            Check(!trust.Validate(origin, changed, chain, SslPolicyErrors.RemoteCertificateChainErrors) &&
                trust.GetReview(origin.ToString())?.PreviousFingerprint == review.Fingerprint, "证书变化阻断连接并保留旧指纹供核对");
            using var expired = CreateCertificate(key, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-2));
            chain.Build(expired);
            Check(!trust.Validate(origin, expired, chain, SslPolicyErrors.RemoteCertificateChainErrors) &&
                trust.GetReview(origin.ToString())?.CanTrust == false, "过期证书不提供信任绕过");
            await CheckHttpsProbeAsync(directory, certificate);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static X509Certificate2 CreateCertificate(RSA key, DateTimeOffset from, DateTimeOffset to)
    {
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSelfSigned(from, to);
    }

    private static async Task CheckHttpsProbeAsync(string directory, X509Certificate2 certificate)
    {
        using var serverCertificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.UserKeySet);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var requests = new List<string>();
        var server = Task.Run(async () =>
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    using var socket = await listener.AcceptTcpClientAsync(cancellation.Token);
                    using var tls = new SslStream(socket.GetStream());
                    try
                    {
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = serverCertificate }, cancellation.Token);
                        using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
                        var first = await reader.ReadLineAsync(cancellation.Token);
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellation.Token))) { }
                        requests.Add(first!);
                        await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), cancellation.Token);
                    }
                    catch (Exception error) when (error is AuthenticationException or IOException) { }
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            var endpoint = "https://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            var trust = new ServerCertificateTrust(directory);
            using var client = new HttpClient(trust.Configure(new HttpClientHandler { UseProxy = false }));
            var resolver = new ServerEndpointResolver(client, trust);
            var initial = await resolver.ResolveAsync(endpoint, cancellation.Token);
            Check(!initial.IsResolved && initial.CertificateIssue is { CanTrust: true } && requests.Count == 0,
                "实际 TLS 探测在信任前不发送 HTTP 请求或登录凭据");
            resolver.TrustCertificate(initial.CertificateIssue!);
            var accepted = await resolver.ResolveAsync(endpoint, cancellation.Token);
            Check(accepted.IsResolved && requests.Count == 1 && requests[0].StartsWith("OPTIONS /api/v1.0/auth/login"),
                "明确确认后实际 HTTPS 登录端点探测成功");
            using var upload = new HttpClient(new ServerCertificateUploadHandler(trust));
            using var uploadResponse = await upload.GetAsync(endpoint, cancellation.Token);
            Check(uploadResponse.StatusCode == HttpStatusCode.MethodNotAllowed && requests.Count == 2,
                "上传连接复用指定服务器的证书信任记录");
        }
        finally
        {
            cancellation.Cancel();
            await server;
            listener.Stop();
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
