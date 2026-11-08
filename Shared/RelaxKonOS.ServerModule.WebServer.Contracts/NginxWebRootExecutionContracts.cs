using RelaxKonOS.Protocol.WebServers;

namespace RelaxKonOS.Server.WebServer;

public enum WebRootMarkerState { Missing, Owned, Conflict }

public sealed record WebRootSitePlan(string? Problem, IReadOnlyList<WebServerSiteDto> Sites);

public interface INginxWebRootExecutor
{
    Task<string> EnableAsync(CancellationToken cancellationToken);
}

public interface INginxWebRootPorts
{
    Task<WebRootSitePlan> PrepareAsync(CancellationToken ct);
    WebRootMarkerState ReadMarker();
    Task<bool> StageMarkerAsync(CancellationToken ct);
    Task<bool> PublishMarkerAsync(CancellationToken ct);
    Task CleanMarkerStageAsync();
    Task<string?> ApplySiteAsync(WebServerSiteDto site, CancellationToken ct);
    void ReportEnabled(int siteCount);
}
