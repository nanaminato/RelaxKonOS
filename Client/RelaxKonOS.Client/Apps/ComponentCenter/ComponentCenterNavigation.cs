namespace RelaxKonOS.Client.Apps.ComponentCenter;

public sealed record ComponentCenterTarget(string HostId, string AppId, string ComponentId, string? WorkloadId, ComponentCenterPage Page, string? ModuleId = null);

/// <summary>Closed display-only routes. HostId is the authenticated client's stable server identity.</summary>
public static class ComponentCenterNavigation
{
    public static Uri ServerModules(string hostId, string? moduleId = null)
    {
        var uri = new Uri("relaxkonos://components/server-modules?hostId=" + Uri.EscapeDataString(hostId)
            + (moduleId is null ? "" : "&moduleId=" + Uri.EscapeDataString(moduleId)));
        if (!TryParse(uri, out _)) throw new ArgumentException("Invalid module navigation target.");
        return uri;
    }
    public static Uri Component(string hostId, string appId, string componentId, ComponentCenterPage page = ComponentCenterPage.All) => Build(hostId, appId, componentId, null, page);
    public static Uri Workload(string hostId, string appId, string workloadId)
    {
        var workload = ComponentCatalog.Workloads.SingleOrDefault(w => w.WorkloadId == workloadId)
            ?? throw new ArgumentException("Unknown workload.", nameof(workloadId));
        return Build(hostId, appId, workload.RequiredComponents.First(), workloadId);
    }
    private static Uri Build(string hostId, string appId, string componentId, string? workloadId, ComponentCenterPage page = ComponentCenterPage.All)
    {
        var uri = new Uri($"relaxkonos://components/{Uri.EscapeDataString(componentId)}?hostId={Uri.EscapeDataString(hostId)}&appId={Uri.EscapeDataString(appId)}"
            + (workloadId is null ? "" : "&workloadId=" + Uri.EscapeDataString(workloadId))
            + (page == ComponentCenterPage.All ? "" : "&page=" + page));
        if (!TryParse(uri, out _)) throw new ArgumentException("Invalid component navigation target.");
        return uri;
    }
    public static bool TryParse(Uri uri, out ComponentCenterTarget? target)
    {
        target = null;
        if (uri.IsAbsoluteUri && uri.Scheme == "relaxkonos" && uri.Host == "components" && uri.AbsolutePath == "/server-modules"
            && uri.Port == -1 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.OriginalString.Length <= 4096
            && uri.IsWellFormedOriginalString())
        {
            var moduleFields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in uri.Query.TrimStart('?').Split('&'))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2 || parts[0] is not ("hostId" or "moduleId") || !moduleFields.TryAdd(parts[0], Uri.UnescapeDataString(parts[1]))) return false;
            }
            if (!moduleFields.TryGetValue("hostId", out var hostId) || string.IsNullOrWhiteSpace(hostId) || hostId.Length > 2048 || hostId.Any(char.IsControl)) return false;
            moduleFields.TryGetValue("moduleId", out var moduleId);
            if (moduleId is not null && (moduleId.Length is 0 or > 128 || moduleId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))) return false;
            target = new(hostId, "", "", null, ComponentCenterPage.ServerModules, moduleId);
            return true;
        }
        if (!uri.IsAbsoluteUri || uri.Scheme != "relaxkonos" || uri.Host != "components" || uri.Port != -1
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.OriginalString.Length > 4096
            || !uri.IsWellFormedOriginalString()) return false;
        var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path.Length != 1 || ComponentCatalog.Find(path[0]) is not { } component
            || uri.AbsolutePath != "/" + component.ComponentId) return false;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || parts[0] is not ("hostId" or "appId" or "workloadId" or "page")
                || !fields.TryAdd(parts[0], Uri.UnescapeDataString(parts[1]))) return false;
        }
        if (!fields.TryGetValue("hostId", out var host) || string.IsNullOrWhiteSpace(host) || host.Length > 2048
            || host.Any(char.IsControl) || !fields.TryGetValue("appId", out var app)
            || !component.Applications.Contains(app, StringComparer.Ordinal)) return false;
        fields.TryGetValue("workloadId", out var workload);
        if (workload is not null && !ComponentCatalog.Workloads.Any(w => w.WorkloadId == workload
            && (w.RequiredComponents.Contains(component.ComponentId) || w.OptionalComponents.Contains(component.ComponentId)))) return false;
        var page = workload is null ? ComponentCenterPage.All : ComponentCenterPage.Workloads;
        if (fields.TryGetValue("page", out var pageText) && (!Enum.TryParse(pageText, out page) || !Enum.IsDefined(page) || pageText != page.ToString())) return false;
        if (page == ComponentCenterPage.ServerModules) return false;
        target = new(host, app, component.ComponentId, workload, page);
        return true;
    }
}
