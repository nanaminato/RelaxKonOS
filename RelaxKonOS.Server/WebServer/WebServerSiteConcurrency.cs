using RelaxKonOS.Protocol.WebServers;

namespace RelaxKonOS.Server.WebServer;

/// <summary>Called under the provider mutation gate before any configuration/permission side effect.</summary>
internal static class WebServerSiteConcurrency
{
    internal static void RequireCurrent(WebServerSiteDto? existing, DateTimeOffset? expected)
    {
        if (expected is null)
        {
            if (existing is not null) throw new NginxWebServerManager.WebServerSiteConflictException("webserver.site_already_exists");
            return;
        }
        if (existing is null || expected == default(DateTimeOffset) || existing.UpdatedAt != expected)
            throw new NginxWebServerManager.WebServerSiteConflictException("webserver.site_changed");
    }
}
