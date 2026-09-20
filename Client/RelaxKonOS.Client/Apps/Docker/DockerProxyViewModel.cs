using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Client.Apps.Docker;

/// <summary>
/// Network proxy page of the Docker Manager. The preference is host-global: it configures the
/// machine's Docker daemon and the Server's own docker child processes, so it is not tied to the
/// signed-in user. Both layers are reported separately because they use unrelated host mechanisms.
/// </summary>
public sealed partial class DockerProxyViewModel(IRemoteDockerClient client) : ObservableObject
{
    /// <summary>
    /// Asked before a change that restarts the Docker daemon, which interrupts running containers.
    /// The page has no window of its own, so the host supplies the dialog.
    /// </summary>
    public Func<string, Task<bool>> RequestConfirmationAsync { get; set; } = _ => Task.FromResult(false);

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _useManagedProxy;
    [ObservableProperty] private string _httpProxy = string.Empty;
    [ObservableProperty] private string _httpsProxy = string.Empty;
    [ObservableProperty] private string _noProxy = string.Empty;
    [ObservableProperty] private bool _applyToEngine = true;
    [ObservableProperty] private bool _applyToBuild = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string _managedProxyEndpoint = string.Empty;
    [ObservableProperty] private bool _isManagedProxyAvailable;
    [ObservableProperty] private string _platform = string.Empty;
    [ObservableProperty] private LocalizedStatus _engineLayerText;
    [ObservableProperty] private LocalizedStatus _buildLayerText;
    [ObservableProperty] private LocalizedStatus _effectiveText;
    [ObservableProperty] private LocalizedStatus _statusText;
    [ObservableProperty] private LocalizedStatus _problemText;

    /// <summary>Daemon layer state from the last status read, used to decide whether a write restarts Docker.</summary>
    private DockerProxyLayerState _engineLayerState = DockerProxyLayerState.Disabled;

    /// <summary>True when the managed proxy is the source, so the URL fields are not the operator's to edit.</summary>
    public bool UseCustomProxy
    {
        get => !UseManagedProxy;
        set => UseManagedProxy = !value;
    }
    /// <summary>Custom URLs are only meaningful while the proxy is on and not supplied by the runtime.</summary>
    public bool CanEditProxyUrls => IsEnabled && UseCustomProxy;
    /// <summary>True when a write will touch the daemon and therefore restart Docker.</summary>
    public bool RestartsDocker => (IsEnabled && ApplyToEngine) || IsEngineLayerInstalled;

    private bool IsEngineLayerInstalled => _engineLayerState is DockerProxyLayerState.Applied or DockerProxyLayerState.RestartRequired;

    /// <summary>Reads the current preference. Called every time the page is opened.</summary>
    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            Apply(await client.GetProxyStatusAsync());
            ProblemText = default;
        }
        catch (Exception exception)
        {
            ProblemText = LocalizedText.Ref("docker.proxy.status.load_failed", exception.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsSaving) return;
        ProblemText = default;

        // The daemon layer restarts Docker, so the operator is warned before anything is sent
        // rather than after the containers have already stopped.
        if (RestartsDocker && !await RequestConfirmationAsync(LocalizedText.Get("docker.proxy.confirm.restart")))
        {
            StatusText = LocalizedText.Ref("docker.proxy.status.cancelled");
            return;
        }

        IsSaving = true;
        StatusText = LocalizedText.Ref("docker.proxy.status.saving");
        try
        {
            var request = new SaveDockerProxySettingsRequest(
                IsEnabled,
                UseManagedProxy ? DockerProxySource.ManagedProxy : DockerProxySource.Custom,
                HttpProxy,
                HttpsProxy,
                NoProxy,
                ApplyToEngine,
                ApplyToBuild,
                Confirmed: true);
            Apply(await client.SaveProxyAsync(request));
            StatusText = LocalizedText.Ref("docker.proxy.status.saved");
        }
        catch (DockerProxyRequestException exception)
        {
            // The problem code is itself a resource key, so it renders as text and an unmapped code
            // still shows verbatim instead of disappearing.
            ProblemText = LocalizedText.Ref(exception.ProblemCode);
        }
        catch (Exception exception)
        {
            ProblemText = LocalizedText.Ref("docker.proxy.status.save_failed", exception.Message);
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (IsSaving) return;
        ProblemText = default;

        if (IsEngineLayerInstalled && !await RequestConfirmationAsync(LocalizedText.Get("docker.proxy.confirm.clear")))
        {
            StatusText = LocalizedText.Ref("docker.proxy.status.cancelled");
            return;
        }

        IsSaving = true;
        StatusText = LocalizedText.Ref("docker.proxy.status.clearing");
        try
        {
            Apply(await client.ClearProxyAsync());
            StatusText = LocalizedText.Ref("docker.proxy.status.cleared");
        }
        catch (Exception exception)
        {
            ProblemText = LocalizedText.Ref("docker.proxy.status.save_failed", exception.Message);
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Copies a server status into the form. The values are already masked of credentials.</summary>
    private void Apply(DockerProxyStatusDto status)
    {
        IsEnabled = status.Settings.Enabled;
        UseManagedProxy = status.Settings.Source == DockerProxySource.ManagedProxy;
        HttpProxy = status.Settings.HttpProxy;
        HttpsProxy = status.Settings.HttpsProxy;
        NoProxy = status.Settings.NoProxy;

        // A host that never saved a preference reports an all-default record, where an unchecked
        // scope would look like a deliberate choice the operator never made.
        var hasPreference = status.Settings.Enabled || status.Settings.ApplyToEngine || status.Settings.ApplyToBuild;
        ApplyToEngine = !hasPreference || status.Settings.ApplyToEngine;
        ApplyToBuild = !hasPreference || status.Settings.ApplyToBuild;

        ManagedProxyEndpoint = status.ManagedProxyEndpoint;
        IsManagedProxyAvailable = status.ManagedProxyAvailable;
        Platform = status.Platform;

        _engineLayerState = status.Layers.FirstOrDefault(layer => layer.Target == DockerProxyTarget.Engine)?.State
            ?? DockerProxyLayerState.Disabled;
        EngineLayerText = DescribeLayer(status, DockerProxyTarget.Engine);
        BuildLayerText = DescribeLayer(status, DockerProxyTarget.Build);
        OnPropertyChanged(nameof(RestartsDocker));

        // The daemon's own read-back, not the saved file: Docker Desktop relays a manual proxy
        // through an internal address, so the reported value can legitimately differ from the input.
        var reported = status.EffectiveHttpProxy.Length > 0 ? status.EffectiveHttpProxy : status.EffectiveHttpsProxy;
        EffectiveText = reported.Length > 0 ? LocalizedStatus.Literal(reported) : LocalizedText.Ref("docker.proxy.effective.empty");
    }

    private static LocalizedStatus DescribeLayer(DockerProxyStatusDto status, DockerProxyTarget target)
    {
        var layer = status.Layers.FirstOrDefault(candidate => candidate.Target == target);
        if (layer is null) return LocalizedText.Ref("docker.proxy.layer.state.unknown");

        var parts = new List<LocalizedStatus> { LocalizedText.Ref(StateKey(layer.State)) };
        if (layer.ProblemCode.Length > 0) parts.Add(LocalizedText.Ref(layer.ProblemCode));
        if (layer.Detail.Length > 0) parts.Add(LocalizedText.Ref(layer.Detail));
        return parts.Count == 1 ? parts[0] : LocalizedStatus.Join(" · ", parts);
    }

    private static string StateKey(DockerProxyLayerState state) => state switch
    {
        DockerProxyLayerState.Disabled => "docker.proxy.layer.state.disabled",
        DockerProxyLayerState.Applied => "docker.proxy.layer.state.applied",
        DockerProxyLayerState.RestartRequired => "docker.proxy.layer.state.restart_required",
        DockerProxyLayerState.Unsupported => "docker.proxy.layer.state.unsupported",
        _ => "docker.proxy.layer.state.failed",
    };

    partial void OnUseManagedProxyChanged(bool value)
    {
        OnPropertyChanged(nameof(UseCustomProxy));
        OnPropertyChanged(nameof(CanEditProxyUrls));
    }

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditProxyUrls));
        OnPropertyChanged(nameof(RestartsDocker));
    }

    partial void OnApplyToEngineChanged(bool value) => OnPropertyChanged(nameof(RestartsDocker));
}
