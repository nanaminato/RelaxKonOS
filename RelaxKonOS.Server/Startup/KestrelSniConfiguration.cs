using Microsoft.Extensions.Primitives;

namespace RelaxKonOS.Server.Startup;

/// <summary>Uses Kestrel's native SNI handshake path for endpoints without explicit SNI rules.</summary>
internal sealed class KestrelSniConfiguration(IConfiguration host, string[] urlEndpoints, bool preferUrls) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new Provider(host, urlEndpoints, preferUrls);

    internal static string[] HostingUrls(IConfiguration host)
    {
        var urls = host["urls"];
        return !string.IsNullOrEmpty(urls) ? Split(urls)
            : [.. Split(host["http_ports"]).Select(port => $"http://*:{port}"),
               .. Split(host["https_ports"]).Select(port => $"https://*:{port}")];
    }

    private static string[] Split(string? value) =>
        value?.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];

    private sealed class Provider : ConfigurationProvider, IDisposable
    {
        private readonly IConfiguration _host;
        private readonly string[] _urlEndpoints;
        private readonly bool _preferUrls;
        private readonly IDisposable _reload;

        internal Provider(IConfiguration host, string[] urlEndpoints, bool preferUrls)
        {
            _host = host;
            _urlEndpoints = urlEndpoints;
            _preferUrls = preferUrls;
            _reload = ChangeToken.OnChange(host.GetReloadToken, () => { Load(); OnReload(); });
        }

        public override void Load()
        {
            var kestrel = _host.GetSection("Kestrel");
            var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in kestrel.AsEnumerable(makePathsRelative: true))
            {
                if (entry.Value is not null && (!_preferUrls || !entry.Key.StartsWith("Endpoints:", StringComparison.OrdinalIgnoreCase)))
                    data[entry.Key] = entry.Value;
            }

            var endpoints = kestrel.GetSection("Endpoints").GetChildren().ToArray();
            if (_preferUrls || endpoints.Length == 0)
            {
                for (var index = 0; index < _urlEndpoints.Length; index++)
                    AddSniDefault(data, $"Endpoints:RelaxKonHostingUrl{index}", _urlEndpoints[index]);
            }
            else
            {
                foreach (var endpoint in endpoints)
                    if (!endpoint.GetSection("Sni").GetChildren().Any())
                        AddSniDefault(data, endpoint.Path["Kestrel:".Length..], endpoint["Url"]);
            }
            Data = data;
        }

        private static void AddSniDefault(Dictionary<string, string?> data, string path, string? url)
        {
            if (url is null) return;
            data[$"{path}:Url"] = url;
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                data[$"{path}:Sni:*"] = "";
        }

        public void Dispose() => _reload.Dispose();
    }
}
