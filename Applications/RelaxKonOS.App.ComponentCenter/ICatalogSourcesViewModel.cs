using System.Windows.Input;

namespace RelaxKonOS.App.ComponentCenter;

public interface ICatalogSourcesViewModel
{
    IEnumerable<ICatalogSourceRow> Sources { get; }
    bool IsBusy { get; }
    bool IsReviewing { get; }
    bool CanEdit { get; }
    string SourceId { get; set; }
    string DisplayName { get; set; }
    string PublisherId { get; set; }
    string BaseUri { get; set; }
    string CatalogPath { get; set; }
    string KeyId { get; set; }
    string PublicKeySpki { get; set; }
    string MaximumValiditySeconds { get; set; }
    bool AllowRoapp { get; set; }
    bool AllowApt { get; set; }
    bool AllowArchive { get; set; }
    bool AllowDownload { get; set; }
    string PrivateNetwork { get; set; }
    string PrivatePort { get; set; }
    string ReviewIdentity { get; }
    string ReviewTrust { get; }
    string ReviewOrigin { get; }
    string ReviewKeys { get; }
    string ReviewProviders { get; }
    string ReviewNetworks { get; }
    string ReviewValidity { get; }
    string ReviewFingerprint { get; }
    string Notice { get; }
    ICommand RefreshCommand { get; }
    ICommand EditCommand { get; }
    ICommand DisableCommand { get; }
    ICommand RemoveCommand { get; }
    ICommand ReviewCommand { get; }
    ICommand ConfirmCommand { get; }
    ICommand CancelReviewCommand { get; }
}

public interface ICatalogSourceRow
{
    string Name { get; }
    string Trust { get; }
    string Status { get; }
    string Sequence { get; }
    string Id { get; }
    string Publisher { get; }
    string Origin { get; }
    string Fingerprints { get; }
    string Providers { get; }
    string Problem { get; }
}
