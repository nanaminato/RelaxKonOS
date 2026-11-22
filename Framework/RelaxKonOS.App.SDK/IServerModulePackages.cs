using System.Windows.Input;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.AppSDK;

public interface IServerModulePackages
{
    Func<string, string> Labels { get; }
    bool HasPackagePlan { get; }
    string PackagePlanPendingText { get; }
    ICommand ReopenPackagePlanCommand { get; }
    ICommand DiscardPackagePlanCommand { get; }
    bool CanManage { get; }
    ICommand OpenStorePageCommand { get; }
    string PackageStatusText { get; }
    bool HasPackageStatus { get; }
    IReadOnlyList<ServerInstalledModuleDto> InstalledPackages { get; }
    bool HasInstalledPackages { get; }
    IReadOnlyList<ServerInstalledModuleDto> SelectedVersions { get; }
    ServerInstalledModuleDto? SelectedPackageVersion { get; set; }
    bool HasSelectedVersions { get; }
    bool HasSelectedVersion { get; }
    string SelectedVersionReleaseText { get; }
    string SelectedVersionDigestText { get; }
    string SelectedVersionStateText { get; }
    string SelectedVersionInstalledAtText { get; }
    ICommand RollbackPackageCommand { get; }
    IReadOnlyList<IServerPackageOperationRow> PackageOperationRows { get; }
    bool HasPackageOperations { get; }
    Task InstallPackageAsync(Stream content, long length);
    void ReportPackageUnavailable();
}

public interface IServerPackageOperationRow
{
    string Title { get; }
    string ProblemText { get; }
    bool HasProblem { get; }
    bool ToneOk { get; }
    bool ToneWarn { get; }
    bool ToneBad { get; }
    string StateText { get; }
    string TimeText { get; }
}
