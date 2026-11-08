namespace RelaxKonOS.Server.WebServer;

public sealed class NginxWebRootExecutor(INginxWebRootPorts ports) : INginxWebRootExecutor
{
    public async Task<string> EnableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var plan = await ports.PrepareAsync(cancellationToken);
            if (plan.Problem is not null) return plan.Problem;
            if (plan.Sites.Count == 0) return "webserver.acme_no_managed_sites";
            var marker = ports.ReadMarker();
            if (marker == WebRootMarkerState.Conflict) return "webserver.ownership_conflict";
            if (marker == WebRootMarkerState.Missing)
            {
                try
                {
                    if (!await ports.StageMarkerAsync(cancellationToken) || !await ports.PublishMarkerAsync(cancellationToken))
                        return "webserver.config_elevation_required";
                }
                finally { await ports.CleanMarkerStageAsync(); }
            }
            foreach (var site in plan.Sites)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var problem = await ports.ApplySiteAsync(site, cancellationToken);
                if (problem is not null) return problem;
            }
            ports.ReportEnabled(plan.Sites.Count);
            return "";
        }
        catch (UnauthorizedAccessException) { return "webserver.config_elevation_required"; }
        catch (IOException) { return "webserver.configuration_changed"; }
    }
}
