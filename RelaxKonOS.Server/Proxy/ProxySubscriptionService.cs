using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.Proxy;

public interface IProxySubscriptionService
{
    Task<IReadOnlyList<ProxySubscriptionDto>> ListAsync(CancellationToken cancellationToken);
    Task<ProxySubscriptionDto> ImportAsync(ImportProxySubscriptionRequest request, CancellationToken cancellationToken);
    Task<string?> RefreshAsync(Guid subscriptionId, CancellationToken cancellationToken);
    Task<string?> RefreshAllAsync(CancellationToken cancellationToken);
    Task<string?> ActivateAsync(Guid subscriptionId, CancellationToken cancellationToken);
    Task<ProxySubscriptionContentDto?> GetContentAsync(Guid subscriptionId, CancellationToken cancellationToken);
    Task<ProxySubscriptionDownloadOptionsDto> GetDownloadOptionsAsync(CancellationToken cancellationToken);
}

/// <summary>Subscription coordinator. URLs stay inside the protected repository and are never logged or returned.</summary>
public sealed class ProxySubscriptionService(
    IProxySubscriptionRepository subscriptions,
    IProxyProfileRepository profiles,
    IProxyConfigurationTransactionService configurations,
    IProxySubscriptionDownloader downloader) : IProxySubscriptionService
{
    public Task<IReadOnlyList<ProxySubscriptionDto>> ListAsync(CancellationToken cancellationToken) => subscriptions.ListAsync(cancellationToken);

    public async Task<ProxySubscriptionDto> ImportAsync(ImportProxySubscriptionRequest request, CancellationToken cancellationToken)
    {
        var source = await downloader.DownloadAsync(request.Url, request.DownloadRoute, cancellationToken);
        var name = NormalizeName(request.Name, source.Uri);
        var profile = await profiles.UpsertAsync(null, name, MihomoEngine.Id, null, cancellationToken);
        try
        {
            var problem = await configurations.StoreAsync(profile.Id, source.Content, cancellationToken);
            if (!string.IsNullOrEmpty(problem)) throw new ProxySubscriptionException(problem);
            return await subscriptions.CreateAsync(name, profile.Id, source.Uri.AbsoluteUri, request.DownloadRoute, cancellationToken);
        }
        catch
        {
            await profiles.DeleteAsync(profile.Id, CancellationToken.None);
            throw;
        }
    }

    public async Task<string?> RefreshAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        var record = await subscriptions.GetAsync(subscriptionId, cancellationToken);
        if (record is null) return ProxyProblemCodes.SubscriptionInvalid;
        var source = await downloader.DownloadAsync(record.Url, record.DownloadRoute, cancellationToken);
        var problem = await configurations.StoreAsync(record.Subscription.ProfileId, source.Content, cancellationToken);
        if (!string.IsNullOrEmpty(problem)) return problem;
        if (record.Subscription.IsActive)
        {
            problem = await configurations.ActivateStoredAsync(record.Subscription.ProfileId, cancellationToken);
            if (!string.IsNullOrEmpty(problem)) return problem;
        }
        await subscriptions.SetLastUpdatedAsync(subscriptionId, DateTimeOffset.UtcNow, cancellationToken);
        return null;
    }

    public async Task<string?> RefreshAllAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in await subscriptions.ListAsync(cancellationToken))
        {
            var problem = await RefreshAsync(subscription.Id, cancellationToken);
            if (!string.IsNullOrEmpty(problem)) return problem;
        }
        return null;
    }

    public async Task<string?> ActivateAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        var record = await subscriptions.GetAsync(subscriptionId, cancellationToken);
        if (record is null) return ProxyProblemCodes.SubscriptionInvalid;
        var problem = await configurations.ActivateStoredAsync(record.Subscription.ProfileId, cancellationToken);
        if (!string.IsNullOrEmpty(problem)) return problem;
        return await profiles.SetActiveAsync(record.Subscription.ProfileId, cancellationToken) is null ? ProxyProblemCodes.SubscriptionInvalid : null;
    }

    public async Task<ProxySubscriptionContentDto?> GetContentAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        var record = await subscriptions.GetAsync(subscriptionId, cancellationToken);
        if (record is null) return null;
        var content = await configurations.ReadStoredAsync(record.Subscription.ProfileId, cancellationToken);
        return content is null ? null : new ProxySubscriptionContentDto(subscriptionId, content, record.Subscription.LastUpdatedAt ?? record.Subscription.CreatedAt);
    }

    public Task<ProxySubscriptionDownloadOptionsDto> GetDownloadOptionsAsync(CancellationToken cancellationToken) =>
        downloader.GetDownloadOptionsAsync(cancellationToken);

    private static string NormalizeName(string? requestedName, Uri source)
    {
        var name = string.IsNullOrWhiteSpace(requestedName)
            ? source.Host + " · " + Guid.NewGuid().ToString("N")[..6]
            : requestedName.Trim();
        if (name.Length is 0 or > 128 || name.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionInvalid);
        return name;
    }
}

public interface IProxySubscriptionDownloader
{
    Task<ProxySubscriptionDownload> DownloadAsync(string url, ProxySubscriptionDownloadRoute downloadRoute, CancellationToken cancellationToken);
    Task<ProxySubscriptionDownloadOptionsDto> GetDownloadOptionsAsync(CancellationToken cancellationToken);
}

public sealed record ProxySubscriptionDownload(Uri Uri, string Content);

/// <summary>Bounded subscription downloader that always rejects loopback, private and link-local targets.</summary>
public sealed class ProxySubscriptionDownloader(
    IHttpClientFactory httpClientFactory,
    IProxySettingsService settingsService,
    IHostEnvironment? environment = null,
    IProxyPlatformPaths? paths = null) : IProxySubscriptionDownloader
{
    private const int MaximumBytes = 1_048_576;
    private const int MaximumDebugCaptures = 10;

    public Task<ProxySubscriptionDownloadOptionsDto> GetDownloadOptionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProxySubscriptionDownloadOptionsDto(ProxySubscriptionNetworkPolicy.HasSystemProxy()));

    public async Task<ProxySubscriptionDownload> DownloadAsync(string url, ProxySubscriptionDownloadRoute downloadRoute, CancellationToken cancellationToken)
    {
        var allowInsecureSources = (await settingsService.GetAsync(cancellationToken)).AllowInsecureSubscriptionSources;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (allowInsecureSources ? uri.Scheme is not ("http" or "https") : uri.Scheme != Uri.UriSchemeHttps) ||
            uri.UserInfo.Length != 0 || uri.IsLoopback)
            throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionInvalid);

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out var literalAddress)) addresses = [literalAddress];
        else
        {
            try { addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken); }
            catch (SocketException) { throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionFetchFailed); }
        }
        if (addresses.Length == 0 || addresses.Any(ProxySubscriptionNetworkPolicy.IsPrivateAddress))
            throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionInvalid);

        if (!Enum.IsDefined(downloadRoute)) throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionInvalid);
        if (downloadRoute == ProxySubscriptionDownloadRoute.SystemProxy && !ProxySubscriptionNetworkPolicy.HasSystemProxy(uri))
            throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionSystemProxyUnavailable);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        // Subscription services commonly select the returned format by User-Agent. Without a
        // Mihomo-compatible value they may return a generic Base64 node list or an HTML page
        // instead of the YAML configuration accepted by the Server's profile importer.
        request.Headers.UserAgent.ParseAdd("clash.meta");
        HttpResponseMessage response;
        var clientName = (downloadRoute, allowInsecureSources) switch
        {
            (ProxySubscriptionDownloadRoute.Direct, false) => "ProxySubscriptionDirect",
            (ProxySubscriptionDownloadRoute.Direct, true) => "ProxySubscriptionDirectInsecureTls",
            (ProxySubscriptionDownloadRoute.SystemProxy, false) => "ProxySubscriptionSystemProxy",
            _ => "ProxySubscriptionSystemProxyInsecureTls",
        };
        var http = httpClientFactory.CreateClient(clientName);
        var transportFailure = downloadRoute == ProxySubscriptionDownloadRoute.SystemProxy
            ? ProxyProblemCodes.SubscriptionSystemProxyUnavailable
            : ProxyProblemCodes.SubscriptionFetchFailed;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (HttpRequestException) { throw new ProxySubscriptionException(transportFailure); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ProxySubscriptionException(transportFailure); }
        using (response)
        {
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaximumBytes)
            throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionFetchFailed);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var bytes = new byte[16_384];
            while (true)
            {
                var read = await stream.ReadAsync(bytes, cancellationToken);
                if (read == 0) break;
                if (buffer.Length + read > MaximumBytes) throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionFetchFailed);
                await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken);
            }
            string content;
            try { content = new UTF8Encoding(false, true).GetString(buffer.ToArray()); }
            catch (DecoderFallbackException) { throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionInvalid); }
            if (string.IsNullOrWhiteSpace(content) || content.IndexOf('\0') >= 0)
                throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionInvalid);
            await CaptureDebugDownloadAsync(content, cancellationToken);
            return new ProxySubscriptionDownload(uri, ProxySubscriptionContentNormalizer.Normalize(content));
        }
        catch (HttpRequestException) { throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionFetchFailed); }
        catch (IOException) { throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionFetchFailed); }
        }
    }

    /// <summary>
    /// Keeps the exact response available for local debugging without weakening production
    /// secrecy. Subscription material may contain credentials, so it is never captured outside
    /// Development and the filename intentionally contains no source URL or subscription name.
    /// </summary>
    private async Task CaptureDebugDownloadAsync(string content, CancellationToken cancellationToken)
    {
        if (environment?.IsDevelopment() != true || paths is null) return;
        try
        {
            var directory = paths.GetSanitizedLogDirectory();
            Directory.CreateDirectory(directory);
            var name = "subscription-download-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N") + ".txt";
            var path = Path.Combine(directory, name);
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            foreach (var stale in Directory.EnumerateFiles(directory, "subscription-download-*.txt", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(File.GetCreationTimeUtc)
                         .Skip(MaximumDebugCaptures))
                File.Delete(stale);
        }
        // Capturing debug material must not interfere with importing a valid subscription.
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

}

internal static class ProxySubscriptionNetworkPolicy
{
    public static bool HasSystemProxy() =>
        HasSystemProxy(new Uri("https://remoteos.invalid/")) || HasSystemProxy(new Uri("http://remoteos.invalid/"));

    public static bool HasSystemProxy(Uri destination)
        => TryGetSystemProxy(HttpClient.DefaultProxy, destination) is not null;

    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        => await ConnectCoreAsync(context.DnsEndPoint, cancellationToken, allowPrivateEndpoint: false);

    /// <summary>Allows only the exact endpoint selected by the Server's system proxy configuration.</summary>
    public static async ValueTask<Stream> ConnectUsingSystemProxyAsync(IWebProxy systemProxy, SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var destination = context.InitialRequestMessage.RequestUri;
        var proxy = destination is null ? null : TryGetSystemProxy(systemProxy, destination);
        var isConfiguredProxy = proxy is not null && EndpointMatches(context.DnsEndPoint, proxy);
        return await ConnectCoreAsync(context.DnsEndPoint, cancellationToken, allowPrivateEndpoint: isConfiguredProxy);
    }

    private static Uri? TryGetSystemProxy(IWebProxy? systemProxy, Uri destination)
    {
        try
        {
            if (systemProxy is null) return null;
            var proxy = systemProxy.GetProxy(destination);
            return proxy is not null && !Uri.Compare(proxy, destination, UriComponents.HttpRequestUrl,
                       UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase).Equals(0)
                ? proxy
                : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or UriFormatException) { return null; }
    }

    private static bool EndpointMatches(DnsEndPoint endpoint, Uri proxy) =>
        endpoint.Port == proxy.Port && string.Equals(endpoint.Host.TrimEnd('.'), proxy.DnsSafeHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static async ValueTask<Stream> ConnectCoreAsync(DnsEndPoint endpoint, CancellationToken cancellationToken, bool allowPrivateEndpoint)
    {
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken); }
        catch (SocketException exception) { throw new HttpRequestException("Subscription host could not be resolved.", exception); }
        var address = addresses.FirstOrDefault(candidate => allowPrivateEndpoint || !IsPrivateAddress(candidate));
        if (address is null) throw new HttpRequestException("Subscription host resolves to a prohibited network.");
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return address.GetAddressBytes()[0] is 0xfc or 0xfd;
        var bytes = address.GetAddressBytes();
        return bytes[0] is 0 or 10 or 127 ||
               (bytes[0] == 169 && bytes[1] == 254) ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
}
