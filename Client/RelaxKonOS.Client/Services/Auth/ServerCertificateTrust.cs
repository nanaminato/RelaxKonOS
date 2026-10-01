using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Http;
using Microsoft.AspNetCore.Http.Connections.Client;

namespace RelaxKonOS.Client.Services.Auth;

public sealed record ServerCertificateReview(string Origin, string Subject, string Issuer,
    string Fingerprint, string? PreviousFingerprint, DateTime NotBefore, DateTime NotAfter,
    string Errors, bool CanTrust);

/// <summary>Explicit leaf-certificate pins scoped to an HTTPS origin, never OS-wide trust.</summary>
public sealed class ServerCertificateTrust
{
    public static ServerCertificateTrust Shared { get; } = new();
    private readonly ConcurrentDictionary<string, string> _pins = new();
    private readonly ConcurrentDictionary<string, ServerCertificateReview> _reviews = new();
    private readonly string _path;
    private readonly object _gate = new();

    public ServerCertificateTrust(string? directory = null)
    {
        _path = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RelaxKonOS", "servercenter"), "tls-certificate-pins.json");
        try
        {
            if (File.Exists(_path))
                foreach (var pin in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? [])
                    _pins[pin.Key] = pin.Value;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private static string Origin(Uri uri) => new UriBuilder("https", uri.Host, uri.Port).Uri.GetLeftPart(UriPartial.Authority);
    public void ClearReview(string endpoint) => _reviews.TryRemove(Origin(new Uri(endpoint)), out _);
    public ServerCertificateReview? GetReview(string endpoint) => _reviews.GetValueOrDefault(Origin(new Uri(endpoint)));

    public bool Validate(Uri uri, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null) return false;
        var origin = Origin(uri);
        var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        var previous = _pins.GetValueOrDefault(origin);
        var now = DateTime.UtcNow;
        var validDates = now >= certificate.NotBefore.ToUniversalTime() && now <= certificate.NotAfter.ToUniversalTime();
        var permittedErrors = (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0;
        var permittedChain = errors == SslPolicyErrors.None || chain is not null && chain.ChainStatus.All(status =>
            (status.Status & ~(X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain)) == 0);
        var canTrust = validDates && permittedErrors && permittedChain;
        if (canTrust && (previous == fingerprint || previous is null && errors == SslPolicyErrors.None))
        {
            _reviews.TryRemove(origin, out _);
            return true;
        }
        _reviews[origin] = new(origin, certificate.Subject, certificate.Issuer, fingerprint, previous,
            certificate.NotBefore, certificate.NotAfter, errors.ToString(), canTrust);
        return false;
    }

    public void Trust(ServerCertificateReview review)
    {
        lock (_gate)
        {
            if (!review.CanTrust || _reviews.GetValueOrDefault(review.Origin) != review)
                throw new InvalidOperationException("The certificate observation is no longer current.");
            var pins = _pins.ToDictionary(p => p.Key, p => p.Value);
            pins[review.Origin] = review.Fingerprint;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(pins));
            File.Move(temporary, _path, overwrite: true);
            _pins[review.Origin] = review.Fingerprint;
            _reviews.TryRemove(review.Origin, out _);
        }
    }

    public HttpMessageHandler Configure(HttpMessageHandler handler)
    {
        if (handler is DelegatingHandler delegating && delegating.InnerHandler is { } inner) Configure(inner);
        if (handler is HttpClientHandler http)
            http.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
                request.RequestUri is { } uri && Validate(uri, certificate, chain, errors);
        return handler;
    }

    public static void ConfigureSignalR(HttpConnectionOptions options, Uri endpoint)
    {
        options.HttpMessageHandlerFactory = handler => Shared.Configure(handler);
        options.WebSocketConfiguration = socket => socket.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
        {
            if (certificate is null) return false;
            using var leaf = new X509Certificate2(certificate);
            return Shared.Validate(endpoint, leaf, chain, errors);
        };
    }
}

public sealed class ServerCertificateHandlerFilter(ServerCertificateTrust trust) : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        trust.Configure(builder.PrimaryHandler);
    };
}

/// <summary>Separate origin pools preserve the upload connect timeout and bind TLS validation to the actual request origin.</summary>
public sealed class ServerCertificateUploadHandler(ServerCertificateTrust trust) : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Lazy<HttpMessageInvoker>> _pools = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var endpoint = new Uri(request.RequestUri!.GetLeftPart(UriPartial.Authority));
        var pool = _pools.GetOrAdd(endpoint.AbsoluteUri, _ => new Lazy<HttpMessageInvoker>(() =>
        {
            var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15), AllowAutoRedirect = false };
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
            {
                if (certificate is null) return false;
                using var leaf = new X509Certificate2(certificate);
                return trust.Validate(endpoint, leaf, chain, errors);
            };
            return new HttpMessageInvoker(handler);
        })).Value;
        return pool.SendAsync(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var pool in _pools.Values)
                if (pool.IsValueCreated) pool.Value.Dispose();
        base.Dispose(disposing);
    }
}

