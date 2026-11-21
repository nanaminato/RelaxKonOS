using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.App.Docker;

public sealed class DockerProxyStatusViewModel(IServerDockerResources resources, DockerText text) : ObservableObject
{
    private DockerProxyStatusDto? _status;
    private string? _loadError;
    private bool _isLoading;

    public DockerText Labels => new(text.Get);
    public bool HasDesktopProxy => _status?.DesktopProxy is not null;
    public string DesktopProxyHttp => _status?.DesktopProxy?.HttpProxy ?? string.Empty;
    public string DesktopProxyHttps => _status?.DesktopProxy?.HttpsProxy ?? string.Empty;
    public string DesktopSettingsPath => _status?.DesktopProxy?.SettingsPath ?? string.Empty;
    public string DesktopProxyModeText => _status?.DesktopProxy is { } desktop
        ? text.Get(desktop.IsManual ? "docker.proxy.desktop.mode.manual" : "docker.proxy.desktop.mode.system")
        : string.Empty;
    public string EngineLayerText => DescribeLayer(DockerProxyTarget.Engine);
    public string BuildLayerText => DescribeLayer(DockerProxyTarget.Build);
    public string EffectiveText => _status switch
    {
        { EffectiveHttpProxy.Length: > 0 } status => status.EffectiveHttpProxy,
        { EffectiveHttpsProxy.Length: > 0 } status => status.EffectiveHttpsProxy,
        not null => text.Get("docker.proxy.effective.empty"),
        _ => string.Empty,
    };
    public string ProblemText => _loadError is null
        ? string.Empty
        : text.Format("docker.proxy.status.load_failed", _loadError);
    public bool HasProblem => _loadError is not null;

    public void RefreshLocalization() => OnPropertyChanged(string.Empty);

    public async Task LoadAsync()
    {
        if (_isLoading) return;
        _isLoading = true;
        try
        {
            _status = await resources.GetProxyStatusAsync();
            _loadError = null;
        }
        catch (Exception exception)
        {
            _status = null;
            _loadError = exception.Message;
        }
        finally
        {
            _isLoading = false;
            OnPropertyChanged(string.Empty);
        }
    }

    private string DescribeLayer(DockerProxyTarget target)
    {
        if (_status is null) return string.Empty;
        var layer = _status.Layers.FirstOrDefault(candidate => candidate.Target == target);
        if (layer is null) return text.Get("docker.proxy.layer.state.unknown");
        var parts = new List<string> { text.Get(DockerProxyLayerLabels.StateKey(layer.State)) };
        if (layer.ProblemCode.Length > 0) parts.Add(text.Get(layer.ProblemCode));
        if (layer.Detail.Length > 0) parts.Add(text.Get(layer.Detail));
        return string.Join(" · ", parts);
    }
}

public static class DockerProxyLayerLabels
{
    public static string StateKey(DockerProxyLayerState state) => state switch
    {
        DockerProxyLayerState.Disabled => "docker.proxy.layer.state.disabled",
        DockerProxyLayerState.Applied => "docker.proxy.layer.state.applied",
        DockerProxyLayerState.RestartRequired => "docker.proxy.layer.state.restart_required",
        DockerProxyLayerState.Unsupported => "docker.proxy.layer.state.unsupported",
        _ => "docker.proxy.layer.state.failed",
    };
}
