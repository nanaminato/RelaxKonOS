using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public interface IComponentReleaseClient
{
    Task<ComponentDownloadRelease[]> ReleasesAsync(string componentId, bool refresh, CancellationToken ct);
}
public enum ComponentDownloadMethod { ServerDownload, ClientDownload, LocalFile, ServerFile }
public sealed record ComponentDownloadMethodChoice(ComponentDownloadMethod Method)
{
    public LocalizedStatus Text => LocalizedStatus.Key("component_center.download.method." + Method);
}

public partial class ComponentCenterViewModel
{
    private readonly Dictionary<string, ComponentDownloadRelease[]> _officialReleases = new();
    [ObservableProperty] private ComponentDownloadRelease? selectedOfficialRelease;
    [ObservableProperty] private string officialVersion = "";
    [ObservableProperty] private bool isLoadingOfficialReleases;
    [ObservableProperty] private bool isInstallingOfficial;
    [ObservableProperty] private LocalizedStatus officialDownloadNotice = LocalizedStatus.Literal("");
    [ObservableProperty] private ComponentDownloadMethodChoice selectedDownloadMethod = new(ComponentDownloadMethod.ServerDownload);
    public IReadOnlyList<ComponentDownloadMethodChoice> DownloadMethods { get; } = Enum.GetValues<ComponentDownloadMethod>().Select(method => new ComponentDownloadMethodChoice(method)).ToArray();
    public IReadOnlyList<ComponentDownloadRelease> OfficialReleases => SelectedDefinition is { } definition
        ? _officialReleases.GetValueOrDefault(definition.ComponentId) ?? [] : [];
    public bool ShowOfficialDownloads => _lifecycle is IComponentReleaseClient && HostPlatform == "windows"
        && SelectedDefinition?.ComponentId is "nginx" or "mihomo" or "frpc" or "frps";
    public bool CanRefreshOfficial => ShowOfficialDownloads && Runtime.CanRead && !Runtime.IsInvalidated && !IsLoadingOfficialReleases && !IsInstallingOfficial;
    public bool CanInstallOfficial => ShowOfficialDownloads && Runtime.CanManage && !Runtime.IsInvalidated && !IsInstallingOfficial && !IsLoadingOfficialReleases
        && !string.IsNullOrWhiteSpace(OfficialVersion) && PackageInstallation is not null && !_packageInstallations.Values.Any(task => task.IsActive)
        && (SelectedDownloadMethod.Method != ComponentDownloadMethod.ClientDownload || SelectedOfficialRelease?.Version == OfficialVersion.Trim());
    public bool ShowManualVersion => SelectedDownloadMethod.Method is ComponentDownloadMethod.LocalFile or ComponentDownloadMethod.ServerFile || OfficialReleases.Count == 0;
    public string OfficialDownloadUrl => SelectedOfficialRelease?.DownloadUrl ?? "";
    public Func<InstallationTaskViewModel, Task<string?>>? SelectLocalComponentPackageAsync { get; set; }
    public Func<InstallationTaskViewModel, Task<string?>>? SelectServerComponentPackageAsync { get; set; }
    public Func<string, Task>? OpenComponentDownloadAsync { get; set; }
    partial void OnSelectedOfficialReleaseChanged(ComponentDownloadRelease? value)
    {
        if (value is not null) OfficialVersion = value.Version;
        OnPropertyChanged(nameof(OfficialDownloadUrl));
    }
    partial void OnOfficialVersionChanged(string value) => OnPropertyChanged(nameof(CanInstallOfficial));
    partial void OnSelectedDownloadMethodChanged(ComponentDownloadMethodChoice value)
    { OnPropertyChanged(nameof(ShowManualVersion)); OnPropertyChanged(nameof(CanInstallOfficial)); }
    partial void OnIsLoadingOfficialReleasesChanged(bool value) => NotifyOfficialDownloads();
    partial void OnIsInstallingOfficialChanged(bool value) => NotifyOfficialDownloads();
    private void NotifyOfficialDownloads()
    {
        foreach (var name in new[] { nameof(ShowOfficialDownloads), nameof(OfficialReleases), nameof(CanRefreshOfficial), nameof(CanInstallOfficial), nameof(ShowManualVersion), nameof(OfficialDownloadUrl) })
            OnPropertyChanged(name);
    }
    public async Task LoadOfficialReleasesAsync(bool refresh)
    {
        if (!CanRefreshOfficial || SelectedDefinition is not { } definition || _lifecycle is not IComponentReleaseClient client) return;
        var component = definition.ComponentId;
        SelectedOfficialRelease = null;
        OfficialVersion = "";
        OfficialDownloadNotice = LocalizedStatus.Literal("");
        IsLoadingOfficialReleases = true;
        try
        {
            var releases = await client.ReleasesAsync(component, refresh, _inspectionLifetime.Token);
            _officialReleases[component] = releases;
            if (SelectedDefinition?.ComponentId != component) return;
            SelectedOfficialRelease = component == "nginx"
                ? releases.FirstOrDefault(release => Version.TryParse(release.Version, out var version) && version.Minor % 2 == 0) ?? releases.FirstOrDefault()
                : releases.FirstOrDefault();
            if (releases.Length == 0) OfficialDownloadNotice = LocalizedStatus.Key("component_center.download.empty");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or OperationCanceledException)
        {
            _officialReleases.Remove(component);
            if (SelectedDefinition?.ComponentId == component) OfficialDownloadNotice = LocalizedStatus.Key("component_center.download.failed");
        }
        finally { IsLoadingOfficialReleases = false; NotifyOfficialDownloads(); }
    }
    [RelayCommand] private Task RefreshOfficialReleasesAsync() => LoadOfficialReleasesAsync(true);
    [RelayCommand] private Task OpenOfficialDownloadAsync() => SelectedOfficialRelease is { } release && OfficialReleases.Contains(release)
        ? OpenComponentDownloadAsync?.Invoke(release.DownloadUrl) ?? Task.CompletedTask : Task.CompletedTask;
    [RelayCommand] private async Task InstallOfficialAsync()
    {
        if (!CanInstallOfficial || SelectedDefinition is not { } definition || PackageInstallation is not { } task) return;
        var component = definition.ComponentId;
        var version = OfficialVersion.Trim();
        if (component != "nginx" && !version.StartsWith('v')) version = "v" + version;
        var method = SelectedDownloadMethod.Method;
        var release = SelectedOfficialRelease;
        IsInstallingOfficial = true;
        try
        {
            string? reference = method switch
            {
                ComponentDownloadMethod.LocalFile => SelectLocalComponentPackageAsync is { } local ? await local(task) : null,
                ComponentDownloadMethod.ServerFile => SelectServerComponentPackageAsync is { } server ? await server(task) : null,
                ComponentDownloadMethod.ClientDownload when release is not null => await task.DownloadAndUploadPackageAsync(release.DownloadUrl, release.FileName),
                _ => null
            };
            if (method != ComponentDownloadMethod.ServerDownload && reference is null) return;
            if (SelectedDefinition?.ComponentId != component || Runtime.IsInvalidated || !Runtime.CanManage) return;
            object request = component switch
            {
                "nginx" => new NginxInstallationRequest(true, Version: version, FileReferenceId: reference),
                "mihomo" => new MihomoInstallationRequest(true, Version: version, FileReferenceId: reference),
                _ => new FrpInstallationRequest(true, Version: version, FileReferenceId: reference)
            };
            await task.SubmitAsync(InstallationOperationKind.Install, request);
        }
        finally { IsInstallingOfficial = false; }
    }
}
