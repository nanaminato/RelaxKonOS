using System.Net;
using System.Net.Sockets;

namespace RelaxKonOS.ManagedRuntimes;

/// <summary>Direct HTTPS only. Every redirect and DNS answer is checked; sockets connect to validated IPs.</summary>
public sealed class RuntimeMetadataDownloader
{
    public async Task<byte[]> DownloadAsync(RuntimeSource source, CancellationToken cancellationToken)
    {
        source.Validate();
        using var output = new MemoryStream();
        await RuntimeHttpsTransfer.CopyAsync(source, new Uri(new Uri(source.BaseUri), source.ManifestPath),
            source.MaximumMetadataBytes, metadata: true, output, cancellationToken);
        return output.ToArray();
    }
}

internal static class RuntimeHttpsTransfer
{
    internal static async Task CopyAsync(RuntimeSource source, Uri uri, long maximumBytes, bool metadata,
        Stream output, CancellationToken cancellationToken)
    {
        source.Validate();
        if (!source.Enabled) throw new RuntimePolicyException("runtime.source_disabled");
        if (maximumBytes is <= 0 or > 2L * 1024 * 1024 * 1024) throw new RuntimePolicyException("runtime.artifact_invalid");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(metadata ? TimeSpan.FromSeconds(45) : TimeSpan.FromMinutes(4));
        try
        {
            using var handler = new SocketsHttpHandler
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
            };
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            for (var hop = 0; hop <= 3; hop++)
            {
                var allowed = hop == 0
                    ? (metadata ? RuntimeValidation.Within(source.BaseUri, uri) : source.AllowsDownload(uri))
                    : (metadata && RuntimeValidation.Within(source.BaseUri, uri) || source.AllowsRedirect(uri));
                if (!RuntimeValidation.Https(uri.OriginalString, out _)
                    || !allowed)
                    throw new RuntimePolicyException("runtime.redirect_forbidden");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("RelaxKonOS-RuntimeCatalog/1.0");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
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
                    throw new RuntimePolicyException("runtime.source_rate_limited");
                if (!response.IsSuccessStatusCode) throw new RuntimePolicyException("runtime.source_http_failed");
                if (response.Content.Headers.ContentLength > maximumBytes
                    || response.Content.Headers.ContentEncoding.Count != 0)
                    throw new RuntimePolicyException(metadata ? "runtime.metadata_too_large" : "runtime.archive_too_large");
                await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
                var buffer = new byte[16384];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    if ((total += read) > maximumBytes)
                        throw new RuntimePolicyException(metadata ? "runtime.metadata_too_large" : "runtime.archive_too_large");
                    try { await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token); }
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
                if (inner is RuntimePolicyException policy) throw new RuntimePolicyException(policy.ProblemCode);
            throw new RuntimePolicyException("runtime.network_unavailable");
        }
        catch (IOException) { throw new RuntimePolicyException("runtime.network_unavailable"); }
    }
}
