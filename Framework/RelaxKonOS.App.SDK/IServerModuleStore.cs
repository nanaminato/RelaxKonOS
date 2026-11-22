using System.Windows.Input;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.AppSDK;

public interface IServerModuleStore
{
    bool ShowStoreFirstRun { get; }
    ICommand RefreshCatalogsCommand { get; }
    bool CanManage { get; }
    bool HasCatalogRevision { get; }
    string CatalogRevision { get; }
    IReadOnlyList<IServerModuleStoreSourceRow> StoreSourceRows { get; }
    bool HasStoreSources { get; }
    bool ShowStoreSourcesEmpty { get; }
    string StoreQuery { get; set; }
    ICommand SearchStoreCommand { get; }
    string StoreStatusText { get; }
    bool HasStoreStatus { get; }
    bool ShowStoreSearchHint { get; }
    IReadOnlyList<ServerModuleStoreItemDto> StoreItems { get; }
    ServerModuleStoreItemDto? SelectedStoreItem { get; set; }
    bool HasStoreItems { get; }
    bool ShowStoreEmpty { get; }
    bool HasSelectedStoreItem { get; }
    string SelectedStoreModuleText { get; }
    string SelectedStoreVersionText { get; }
    string SelectedStorePublisherText { get; }
    string SelectedStoreSourceText { get; }
    string SelectedStoreRidText { get; }
    string SelectedStoreDigestText { get; }
    string SelectedStoreArchiveText { get; }
    string SelectedStoreTrustText { get; }
    string SelectedStoreDependenciesText { get; }
    string SelectedStoreCapabilitiesText { get; }
    bool CanInstallStoreItem { get; }
    string SelectedStoreCompatibilityText { get; }
    bool HasSelectedStoreBlockers { get; }
    string SelectedStoreBlockedText { get; }
    ICommand InstallStoreItemCommand { get; }
}

public interface IServerModuleStoreSourceRow
{
    string SourceId { get; }
    bool HasProblem { get; }
    string StatusText { get; }
    string CountText { get; }
    string ProblemText { get; }
}
