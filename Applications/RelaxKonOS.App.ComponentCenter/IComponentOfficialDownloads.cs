using System.Windows.Input;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.App.ComponentCenter;

public interface IComponentDesktopPackage
{
    string DesktopCompatibilityText { get; }
    ICommand ReviewDesktopCatalogPackageCommand { get; }
}

public interface IComponentOfficialDownloads : IComponentInstallationProgress
{
    ICommand RefreshOfficialReleasesCommand { get; }
    bool CanRefreshOfficial { get; }
    bool IsLoadingOfficialReleases { get; }
    IReadOnlyList<IComponentDownloadMethodChoice> DownloadMethods { get; }
    IComponentDownloadMethodChoice SelectedDownloadMethod { get; set; }
    bool IsInstallingOfficial { get; }
    bool ShowOnlineVersions { get; }
    IReadOnlyList<ComponentDownloadRelease> OfficialReleases { get; }
    ComponentDownloadRelease? SelectedOfficialRelease { get; set; }
    bool ShowManualVersion { get; }
    string OfficialVersion { get; set; }
    bool IsFileSource { get; }
    string SelectedPackageFileText { get; }
    ICommand SelectPackageFileCommand { get; }
    bool IsSelectingPackageFile { get; }
    string OfficialDownloadNotice { get; }
    string OfficialDownloadUrl { get; }
    ICommand OpenOfficialDownloadCommand { get; }
    ICommand InstallOfficialCommand { get; }
    bool CanInstallOfficial { get; }
}

public interface IComponentDownloadMethodChoice
{
    string Text { get; }
}
