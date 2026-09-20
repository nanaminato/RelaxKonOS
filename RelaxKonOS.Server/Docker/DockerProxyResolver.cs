using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.Storage.Sqlite;

namespace RelaxKonOS.Server.Docker;

/// <summary>
/// The effective outbound-proxy values for this host, resolved from the shared preference. Docker
/// layers and host-initiated download clients read the same resolution, so a saved setting can
/// never be applied to one consumer and silently skipped for another.
/// </summary>
public sealed record DockerProxyResolution(
    bool Enabled,
    DockerProxySource Source,
    string HttpProxy,
    string HttpsProxy,
    string NoProxy,
    bool ApplyToBuild,
    bool ApplyToEngine,
    bool ApplyToImageTags,
    bool ApplyToRuntimeDownloads,
    string ManagedProxyEndpoint,
    bool ManagedProxyAvailable,
    string ProblemCode)
{
    /// <summary>Nothing is configured and nothing may be applied.</summary>
    public static DockerProxyResolution Disabled(string problemCode = "") =>
        new(false, DockerProxySource.Custom, "", "", "", false, false, false, false, "", false, problemCode);

    /// <summary>True when the resolution is usable, so a layer may actually be installed.</summary>
    public bool IsUsable => Enabled && ProblemCode.Length == 0;
    public bool BuildLayerActive => IsUsable && ApplyToBuild;
    public bool EngineLayerRequested => IsUsable && ApplyToEngine;
    public bool ImageTagsActive => IsUsable && ApplyToImageTags;
    public bool RuntimeDownloadsActive => IsUsable && ApplyToRuntimeDownloads;
}

public interface IDockerProxyResolver
{
    Task<DockerProxyResolution> ResolveAsync(CancellationToken cancellationToken = default);
    /// <summary>Drops the cached resolution after the saved setting changes.</summary>
    void Invalidate();
}

/// <summary>
/// Reads the saved preference and turns it into concrete values. The public API never sends a proxy
/// endpoint, mirroring the image mirror resolver: the value is a server-side decision.
/// </summary>
public sealed class DockerProxyResolver(IDockerProxySettingsRepository settings, IProxySettingsService proxySettings) : IDockerProxyResolver
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private (DateTimeOffset At, DockerProxyResolution Value)? _cache;

    /// <summary>
    /// Resolutions are cached briefly because every docker command asks for one, and a single
    /// manager refresh issues several. <see cref="Invalidate"/> keeps a save immediately visible.
    /// </summary>
    public async Task<DockerProxyResolution> ResolveAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
            if (_cache is { } cached && DateTimeOffset.UtcNow - cached.At < CacheLifetime) return cached.Value;

        var resolved = await ResolveCoreAsync(cancellationToken);
        lock (_gate) _cache = (DateTimeOffset.UtcNow, resolved);
        return resolved;
    }

    /// <summary>Drops the cached resolution, so the next read observes a just-saved setting.</summary>
    public void Invalidate()
    {
        lock (_gate) _cache = null;
    }

    private async Task<DockerProxyResolution> ResolveCoreAsync(CancellationToken cancellationToken)
    {
        var (managedEndpoint, managedAvailable) = await ProbeManagedProxyAsync(cancellationToken);

        DockerProxySetting? saved;
        try { saved = await settings.GetAsync(cancellationToken); }
        catch (DockerProxySecretUnreadableException) { return DockerProxyResolution.Disabled(DockerProxyProblem.StoredValueUnreadable); }

        if (saved is null || !saved.Enabled)
            return DockerProxyResolution.Disabled() with { ManagedProxyEndpoint = managedEndpoint, ManagedProxyAvailable = managedAvailable };

        var bypassListIsValid = DockerProxyValidation.TryNormalizeBypassList(saved.NoProxy, out var noProxy)
            && DockerProxyValidation.IsValidBypassList(noProxy);
        string httpProxy, httpsProxy;
        if (saved.Source == DockerProxySource.ManagedProxy)
        {
            if (!managedAvailable)
                return DockerProxyResolution.Disabled(DockerProxyProblem.ManagedProxyUnavailable) with
                {
                    Enabled = true, Source = saved.Source, ApplyToBuild = saved.ApplyToBuild, ApplyToEngine = saved.ApplyToEngine,
                    ApplyToImageTags = saved.ApplyToImageTags, ApplyToRuntimeDownloads = saved.ApplyToRuntimeDownloads,
                    ManagedProxyEndpoint = managedEndpoint, ManagedProxyAvailable = false, NoProxy = noProxy,
                };
            httpProxy = httpsProxy = managedEndpoint;
        }
        else
        {
            httpProxy = saved.HttpProxy?.Trim() ?? string.Empty;
            httpsProxy = saved.HttpsProxy?.Trim() ?? string.Empty;
            // An empty HTTPS proxy means "reuse the HTTP proxy".
            if (httpsProxy.Length == 0) httpsProxy = httpProxy;
        }

        if (!DockerProxyValidation.IsValidProxyUrl(httpProxy) || !DockerProxyValidation.IsValidProxyUrl(httpsProxy)
            || !bypassListIsValid)
            return DockerProxyResolution.Disabled(DockerProxyProblem.ConfigurationInvalid) with
            {
                Enabled = true, Source = saved.Source, ApplyToBuild = saved.ApplyToBuild, ApplyToEngine = saved.ApplyToEngine,
                ApplyToImageTags = saved.ApplyToImageTags, ApplyToRuntimeDownloads = saved.ApplyToRuntimeDownloads,
                ManagedProxyEndpoint = managedEndpoint, ManagedProxyAvailable = managedAvailable, NoProxy = noProxy,
            };

        return new(true, saved.Source, httpProxy, httpsProxy, noProxy, saved.ApplyToBuild, saved.ApplyToEngine, saved.ApplyToImageTags, saved.ApplyToRuntimeDownloads,
            managedEndpoint, managedAvailable, "");
    }

    /// <summary>
    /// The managed proxy endpoint is the runtime's own listener, so it is read from the proxy
    /// settings rather than duplicated here. Availability is a live socket check: proxying to a
    /// port nobody is listening on turns a working direct connection into a hard failure.
    /// </summary>
    private async Task<(string Endpoint, bool Available)> ProbeManagedProxyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var current = await proxySettings.GetAsync(cancellationToken);
            var host = string.IsNullOrWhiteSpace(current.SystemProxyHost) ? "127.0.0.1" : current.SystemProxyHost.Trim();
            var endpoint = $"http://{FormatHost(host)}:{current.MixedPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            return (endpoint, IsListening(host, current.MixedPort));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            return (string.Empty, false);
        }
    }

    private static string FormatHost(string host) => host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;

    private static bool IsListening(string host, int port)
    {
        try
        {
            // A wildcard listener accepts loopback traffic too, so both are treated as reachable.
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port && (IPAddress.IsLoopback(endpoint.Address) || endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any)));
        }
        catch (NetworkInformationException) { return false; }
        catch (SocketException) { return false; }
    }
}
