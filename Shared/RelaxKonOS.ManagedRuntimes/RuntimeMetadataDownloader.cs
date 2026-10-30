using System.Net;
using System.Net.Sockets;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes;

/// <summary>
/// Direct HTTPS only, with an optional explicit host proxy. Every redirect and DNS answer is checked for
/// a direct connection; sockets connect to validated IPs. A proxied transfer cannot pin the origin IP
/// because the proxy resolves and connects, so only a literal address in the target URL is re-checked
/// there and the proxy endpoint itself is an administrator declaration, never a request value.
/// </summary>
public sealed class RuntimeMetadataDownloader
{
    public async Task<byte[]> DownloadAsync(RuntimeSource source, RuntimeDownloadProxy? proxy, CancellationToken cancellationToken)
    {
        source.Validate();
        using var output = new MemoryStream();
        await RuntimeHttpsTransfer.CopyAsync(source, new Uri(new Uri(source.BaseUri), source.ManifestPath),
            source.MaximumMetadataBytes, metadata: true, output, proxy, cancellationToken);
        return output.ToArray();
    }
}

internal static class RuntimeHttpsTransfer
{
    internal static async Task CopyAsync(IRuntimeDownloadScope source, Uri uri, long maximumBytes, bool metadata,
        Stream output, RuntimeDownloadProxy? proxy, CancellationToken cancellationToken)
    {
        source.Validate();
        if (!source.Enabled) throw new RuntimePolicyException("runtime.source_disabled");
        if (maximumBytes is <= 0 or > 2L * 1024 * 1024 * 1024) throw new RuntimePolicyException("runtime.artifact_invalid");
        if (proxy is not null) RuntimeDownloadPolicy.Validate(proxy);
        // A partial body can only be repeated when the destination can be rewound to its starting offset.
        var origin = output.CanSeek ? output.Position : 0L;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(metadata ? TimeSpan.FromSeconds(45) : TimeSpan.FromMinutes(4));
        for (var attempt = 1; ; attempt++)
        {
            TransientFailure failure;
            try
            {
                await AttemptAsync(source, uri, maximumBytes, metadata, output, proxy, deadline.Token, cancellationToken);
                return;
            }
            catch (TransientFailure ex) { failure = ex; }
            catch (RuntimePolicyException ex) { failure = new(ex.ProblemCode, null); }
            if (attempt >= RuntimeDownloadPolicy.MaximumAttempts || !output.CanSeek
                || !RuntimeDownloadPolicy.IsRetryable(failure.ProblemCode) || cancellationToken.IsCancellationRequested)
                throw new RuntimePolicyException(failure.ProblemCode);
            // A repeated attempt starts from a clean destination, so the caller's digest check still sees one body.
            output.SetLength(origin);
            output.Position = origin;
            try { await Task.Delay(RuntimeDownloadPolicy.Delay(attempt, DateTimeOffset.UtcNow, failure.RetryAfter), deadline.Token); }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
                throw new RuntimePolicyException("runtime.source_timeout");
            }
        }
    }

    /// <summary>One complete attempt, including every redirect hop. A retryable refusal is thrown as a signal.</summary>
    private static async Task AttemptAsync(IRuntimeDownloadScope source, Uri uri, long maximumBytes, bool metadata,
        Stream output, RuntimeDownloadProxy? proxy, CancellationToken deadline, CancellationToken cancellationToken)
    {
        using var clients = new TransferClients(source, proxy);
        try
        {
            for (var hop = 0; hop <= 3; hop++)
            {
                // Archive queries are allowed only on redirects explicitly approved by their source.
                // Initial artifact identities and ordinary manifest scopes still reject queries.
                var queryAllowed = (metadata || hop > 0) && uri.Query.Length != 0 && source.AllowsQuery(uri)
                    && uri.Fragment.Length == 0 && RuntimeValidation.Https(uri.OriginalString[..uri.OriginalString.IndexOf('?')],out _);
                var allowed = hop == 0
                    ? (metadata ? queryAllowed || RuntimeValidation.Within(source.BaseUri, uri) : source.AllowsDownload(uri))
                    : (queryAllowed || metadata && RuntimeValidation.Within(source.BaseUri, uri) || source.AllowsRedirect(uri));
                if (!(queryAllowed || RuntimeValidation.Https(uri.OriginalString, out _))
                    || !allowed)
                    throw new RuntimePolicyException("runtime.redirect_forbidden");
                // A tunnelled hop cannot have its resolved address checked, so a literal address in the
                // target still has to be an allowed one before the proxy is asked to reach it.
                var proxied = clients.Proxied(uri);
                if (proxied && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal))
                    RuntimeNetworkPolicy.RequireAddresses([literal], uri.Port, source.PrivateNetworks);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("RelaxKonOS-RuntimeCatalog/1.0");
                using var response = await clients.SendAsync(proxied, request, deadline);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (hop == 3 || response.Headers.Location is not { } location)
                        throw new RuntimePolicyException("runtime.redirect_forbidden");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                if ((int)response.StatusCode == 429 || response.StatusCode == HttpStatusCode.Forbidden
                    && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0"))
                {
                    // Quota exhaustion resets on a schedule the client cannot influence; a 429 may be short-lived.
                    if ((int)response.StatusCode == 429) throw new TransientFailure("runtime.source_rate_limited", RetryAfter(response));
                    throw new RuntimePolicyException("runtime.source_rate_limited");
                }
                if ((int)response.StatusCode is 408 or 425 or >= 500 and <= 599)
                    throw new TransientFailure("runtime.source_unavailable", RetryAfter(response));
                if (!response.IsSuccessStatusCode) throw new RuntimePolicyException("runtime.source_http_failed");
                if (response.Content.Headers.ContentLength > maximumBytes
                    || response.Content.Headers.ContentEncoding.Count != 0)
                    throw new RuntimePolicyException(metadata ? "runtime.metadata_too_large" : "runtime.archive_too_large");
                await using var input = await response.Content.ReadAsStreamAsync(deadline);
                var buffer = new byte[16384];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, deadline)) > 0)
                {
                    if ((total += read) > maximumBytes)
                        throw new RuntimePolicyException(metadata ? "runtime.metadata_too_large" : "runtime.archive_too_large");
                    try { await output.WriteAsync(buffer.AsMemory(0, read), deadline); }
                    catch (IOException) { throw new RuntimePolicyException("runtime.staging_write_failed"); }
                }
                return;
            }
            throw new RuntimePolicyException("runtime.redirect_forbidden");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new RuntimePolicyException("runtime.source_timeout"); }
        catch (HttpRequestException ex)
        {
            for (Exception? inner = ex; inner is not null; inner = inner.InnerException)
            {
                if (inner is RuntimePolicyException policy) throw new RuntimePolicyException(policy.ProblemCode);
                // A rejected certificate is a trust decision, not a transient network condition: retrying
                // would only repeat the same handshake against the same untrusted endpoint.
                if (inner is System.Security.Authentication.AuthenticationException)
                    throw new RuntimePolicyException("runtime.source_tls_invalid");
            }
            throw new RuntimePolicyException("runtime.network_unavailable");
        }
        catch (IOException) { throw new RuntimePolicyException("runtime.network_unavailable"); }
    }

    /// <summary>The proxy's own hint, bounded by the policy cap at the point it is used.</summary>
    private static DateTimeOffset? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta is { } delta ? DateTimeOffset.UtcNow + delta
        : response.Headers.RetryAfter?.Date is { } date ? date
        : null;

    /// <summary>A failure the policy may repeat. Any other refusal is final on the first attempt.</summary>
    private sealed class TransientFailure(string problemCode, DateTimeOffset? retryAfter) : Exception(problemCode)
    {
        internal string ProblemCode { get; } = problemCode;
        internal DateTimeOffset? RetryAfter { get; } = retryAfter;
    }

    /// <summary>
    /// Owns the two connection shapes for one attempt: the direct client pins validated DNS answers, the
    /// proxied client tunnels to the administrator's endpoint. The choice is made per hop, because a
    /// bypass entry names a target, not a transfer.
    /// </summary>
    private sealed class TransferClients : IDisposable
    {
        private readonly IRuntimeDownloadScope source;
        private readonly RuntimeDownloadProxy? proxy;
        private HttpClient? direct;
        private HttpClient? proxied;

        internal TransferClients(IRuntimeDownloadScope source, RuntimeDownloadProxy? proxy)
        {
            this.source = source;
            this.proxy = proxy;
        }

        internal bool Proxied(Uri uri) => proxy is not null && !RuntimeDownloadPolicy.Bypasses(proxy, uri);

        internal Task<HttpResponseMessage> SendAsync(bool useProxy, HttpRequestMessage request, CancellationToken ct) =>
            (useProxy ? ProxiedClient() : DirectClient()).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        private HttpClient DirectClient() => direct ??= Create(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                RuntimeNetworkPolicy.RequireAddresses(addresses, context.DnsEndPoint.Port, source.PrivateNetworks);
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                        if (socket.RemoteEndPoint is not IPEndPoint remote || !remote.Address.Equals(address))
                            throw new RuntimePolicyException("runtime.address_forbidden");
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (SocketException) { socket.Dispose(); }
                    catch { socket.Dispose(); throw; }
                }
                throw new HttpRequestException("runtime.network_unavailable");
            }
        });

        /// <summary>
        /// No connect callback here on purpose: through a tunnel the proxy performs DNS and the connect,
        /// so an origin pin would be fiction. Proxy credentials stay on the proxy connection and are never
        /// attached to the origin request.
        /// </summary>
        private HttpClient ProxiedClient()
        {
            if (proxied is not null) return proxied;
            var (address, user, password) = RuntimeDownloadPolicy.Decompose(proxy!);
            var webProxy = new WebProxy(address) { BypassProxyOnLocal = false };
            if (user is not null) webProxy.Credentials = new NetworkCredential(user, password);
            return proxied = Create(new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseCookies = false, UseProxy = true, Proxy = webProxy,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(10),
            });
        }

        private static HttpClient Create(SocketsHttpHandler handler) =>
            new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

        public void Dispose()
        {
            direct?.Dispose();
            proxied?.Dispose();
        }
    }
}
