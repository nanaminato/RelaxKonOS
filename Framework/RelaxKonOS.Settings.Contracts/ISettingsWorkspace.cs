using System.Windows.Input;

namespace RelaxKonOS.App.Settings.Views;

/// <summary>Data and commands consumed by the Settings workspace independently of its page providers.</summary>
public interface ISettingsWorkspace : ISettingsNavigationPresentation
{
    IReadOnlyList<SettingsPageModel> NavigationPages { get; }
    SettingsPageModel? SelectedCategory { get; set; }
    string ConnectionSummary { get; }
    string ConnectionAddress { get; }
    string ParentTitle { get; }
    string GrandparentTitle { get; }
    string? ParentRoute { get; }
    bool HasParentPage { get; }
    bool HasGrandparentPage { get; }
    bool CanPinCurrentPage { get; }
    bool IsCurrentPagePinned { get; set; }
    string PageScopeDescription { get; }
    bool HasPageScopeDescription { get; }
    bool IsAppsCategory { get; }
    string CatalogProblem { get; }
    IReadOnlyList<SettingsSearchEntry> SearchResults { get; }
    bool HasNoResults { get; }
    bool HasDeviceSaveFailure { get; }
    bool HasFailureToast { get; }
    string FailureToast { get; }
    bool CanRetry { get; }
    bool CanDiscard { get; }
    ICommand BackCommand { get; }
    ICommand OpenPageCommand { get; }
    ICommand OpenSearchResultCommand { get; }
    ICommand RetryDeviceSaveCommand { get; }
    ICommand RetrySaveCommand { get; }
    ICommand DiscardDraftCommand { get; }
}
