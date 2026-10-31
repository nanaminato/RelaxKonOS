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
public sealed record ComponentPackageSelection(string ReferenceId, string Name);
public enum ComponentDownloadMethod { ServerDownload, ClientDownload, LocalFile, ServerFile }
public sealed record ComponentDownloadMethodChoice(ComponentDownloadMethod Method)
{
    public LocalizedStatus Text => LocalizedStatus.Key("component_center.download.method." + Method);
}

public partial class ComponentCenterViewModel
{
    [ObservableProperty] private ComponentPackageSelection? selectedPackageFile;
    [ObservableProperty] private bool isSelectingPackageFile;
    private long _packageSelectionRevision;
    public bool IsFileSource => SelectedDownloadMethod.Method is ComponentDownloadMethod.LocalFile or ComponentDownloadMethod.ServerFile;
    public bool ShowOnlineVersions => !IsFileSource && OfficialReleases.Count > 0;
    public bool CanSelectPackageFile => ShowOfficialDownloads && IsFileSource && Runtime.CanManage && !Runtime.IsInvalidated
        && !IsInstallingOfficial && !IsSelectingPackageFile && PackageInstallation is not null
        && !_packageInstallations.Values.Any(task => task.IsActive);
    public LocalizedStatus SelectedPackageFileText => SelectedPackageFile is { } file
        ? LocalizedStatus.Literal(file.Name) : LocalizedStatus.Key("component_center.download.no_file");
    partial void OnSelectedPackageFileChanged(ComponentPackageSelection? value) => NotifyOfficialDownloads();
    partial void OnIsSelectingPackageFileChanged(bool value) => NotifyOfficialDownloads();
    private void ClearPackageFile()
    {
        _packageSelectionRevision++;
        SelectedPackageFile = null;
    }
    [RelayCommand(CanExecute = nameof(CanSelectPackageFile))]
    private async Task SelectPackageFileAsync()
    {
        if (!CanSelectPackageFile || PackageInstallation is not { } task) return;
        var revision = _packageSelectionRevision;
        var picker = SelectedDownloadMethod.Method == ComponentDownloadMethod.LocalFile
            ? SelectLocalComponentPackageAsync : SelectServerComponentPackageAsync;
        IsSelectingPackageFile = true;
        try
        {
            var file = picker is not null ? await picker(task) : null;
            if (revision == _packageSelectionRevision && Runtime.CanManage && !Runtime.IsInvalidated && file is not null)
                SelectedPackageFile = file;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException)
        {
            if (revision == _packageSelectionRevision)
                OfficialDownloadNotice = LocalizedStatus.Key("component_center.download.file_failed");
        }
        finally { IsSelectingPackageFile = false; }
    }
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
    /// <summary>
    /// Nginx and FRP keep their official-release flow on Windows only. Mihomo has no package source at
    /// all, so its official release archive is the only installation path on Linux as well.
    /// </summary>
    public bool ShowOfficialDownloads => _lifecycle is IComponentReleaseClient
        && (HostPlatform == "windows" || HostPlatform == "linux" && SelectedDefinition?.ComponentId == "mihomo")
        && SelectedDefinition?.ComponentId is "nginx" or "mihomo" or "frpc" or "frps";
    public bool CanRefreshOfficial => ShowOfficialDownloads && Runtime.CanRead && !Runtime.IsInvalidated && !IsLoadingOfficialReleases && !IsInstallingOfficial && !IsSelectingPackageFile;
    public bool CanInstallOfficial => ShowOfficialDownloads && Runtime.CanManage && !Runtime.IsInvalidated && !IsInstallingOfficial && !IsLoadingOfficialReleases && !IsSelectingPackageFile
        && !string.IsNullOrWhiteSpace(OfficialVersion) && PackageInstallation is not null && !_packageInstallations.Values.Any(task => task.IsActive)
        && (!IsFileSource || SelectedPackageFile is not null)
        && (SelectedDownloadMethod.Method != ComponentDownloadMethod.ClientDownload || SelectedOfficialRelease?.Version == OfficialVersion.Trim());
    public bool ShowManualVersion => SelectedDownloadMethod.Method is ComponentDownloadMethod.LocalFile or ComponentDownloadMethod.ServerFile || OfficialReleases.Count == 0;
    public string OfficialDownloadUrl => SelectedOfficialRelease is { } release && OfficialReleases.Contains(release)
        ? release.DownloadUrl : SelectedDefinition?.ComponentId switch
        {
            "nginx" => "https://nginx.org/en/download.html",
            "mihomo" => "https://github.com/MetaCubeX/mihomo/releases",
            "frpc" or "frps" => "https://github.com/fatedier/frp/releases",
            _ => ""
        };
    public bool CanOpenOfficialDownload => ShowOfficialDownloads && OpenComponentDownloadAsync is not null
        && !string.IsNullOrEmpty(OfficialDownloadUrl);
    public Func<InstallationTaskViewModel, Task<ComponentPackageSelection?>>? SelectLocalComponentPackageAsync { get; set; }
    public Func<InstallationTaskViewModel, Task<ComponentPackageSelection?>>? SelectServerComponentPackageAsync { get; set; }
    private Func<string, Task>? _openComponentDownloadAsync;
    public Func<string, Task>? OpenComponentDownloadAsync
    {
        get => _openComponentDownloadAsync;
        set { _openComponentDownloadAsync = value; NotifyOfficialDownloads(); }
    }
    partial void OnSelectedOfficialReleaseChanged(ComponentDownloadRelease? value)
    {
        if (value is not null) OfficialVersion = value.Version;
        NotifyOfficialDownloads();
    }
    partial void OnOfficialVersionChanged(string value) => OnPropertyChanged(nameof(CanInstallOfficial));
    partial void OnSelectedDownloadMethodChanged(ComponentDownloadMethodChoice value)
    { ClearPackageFile(); if (IsFileSource) OfficialVersion = ""; else if (SelectedOfficialRelease is { } release) OfficialVersion = release.Version; NotifyOfficialDownloads(); }
    partial void OnIsLoadingOfficialReleasesChanged(bool value) => NotifyOfficialDownloads();
    partial void OnIsInstallingOfficialChanged(bool value) => NotifyOfficialDownloads();
    private void NotifyOfficialDownloads()
    {
        foreach (var name in new[] { nameof(ShowOfficialDownloads), nameof(OfficialReleases), nameof(CanRefreshOfficial), nameof(CanInstallOfficial), nameof(ShowManualVersion), nameof(OfficialDownloadUrl), nameof(CanOpenOfficialDownload), nameof(IsFileSource), nameof(ShowOnlineVersions), nameof(CanSelectPackageFile), nameof(SelectedPackageFileText), nameof(IsSelectingPackageFile) })
            OnPropertyChanged(name);
        OpenOfficialDownloadCommand.NotifyCanExecuteChanged();
        SelectPackageFileCommand.NotifyCanExecuteChanged();
    }
    public async Task LoadOfficialReleasesAsync(bool refresh)
    {
        if (!CanRefreshOfficial || SelectedDefinition is not { } definition || _lifecycle is not IComponentReleaseClient client) return;
        var component = definition.ComponentId;
        SelectedOfficialRelease = null;
        if (!IsFileSource) OfficialVersion = "";
        OfficialDownloadNotice = LocalizedStatus.Literal("");
        IsLoadingOfficialReleases = true;
        try
        {
            var releases = await client.ReleasesAsync(component, refresh, _inspectionLifetime.Token);
            _officialReleases[component] = releases;
            if (SelectedDefinition?.ComponentId != component || IsFileSource) return;
            SelectedOfficialRelease = component == "nginx"
                ? releases.FirstOrDefault(release => Version.TryParse(release.Version, out var version) && version.Minor % 2 == 0) ?? releases.FirstOrDefault()
                : releases.FirstOrDefault();
            if (releases.Length == 0) OfficialDownloadNotice = LocalizedStatus.Key("component_center.download.empty");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException)
        {
            _officialReleases.Remove(component);
            if (SelectedDefinition?.ComponentId == component) OfficialDownloadNotice = LocalizedStatus.Key(ex switch
                {
                    OperationCanceledException => "component_center.download.timeout",
                    HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests } => "component_center.download.rate_limited",
                    HttpRequestException => "component_center.download.network_failed",
                    InvalidOperationException { Message: "component.platform_unsupported" } => "component_center.download.unsupported",
                    InvalidOperationException { Message: "component.package_index_unavailable" } => "component_center.download.index_unavailable",
                    InvalidOperationException { Message: "component.permission_denied" } => "component_center.download.permission_denied",
                    Exception { Message: "component.state_unavailable" } => "component_center.download.state_unavailable",
                    _ => "component_center.download.failed"
                });
        }
        finally { IsLoadingOfficialReleases = false; NotifyOfficialDownloads(); }
    }
    [RelayCommand] private Task RefreshOfficialReleasesAsync() => LoadOfficialReleasesAsync(true);
    [RelayCommand(CanExecute = nameof(CanOpenOfficialDownload))]
    private Task OpenOfficialDownloadAsync() => CanOpenOfficialDownload
        ? OpenComponentDownloadAsync!(OfficialDownloadUrl) : Task.CompletedTask;
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
                ComponentDownloadMethod.LocalFile or ComponentDownloadMethod.ServerFile => SelectedPackageFile?.ReferenceId,
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
