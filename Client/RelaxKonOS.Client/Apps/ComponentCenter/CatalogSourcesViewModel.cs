using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.ManagedRuntimes.Catalog;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public sealed record CatalogSourceRow(ComponentCatalogSourceView View)
{
    public string Id => View.Policy.SourceId;
    public string Name => View.Policy.DisplayName;
    public string Publisher => View.Policy.PublisherId;
    public string Origin => View.Policy.BaseUri;
    public string Fingerprints => string.Join("\n", View.Policy.KeyFingerprints);
    public string Providers => string.Join(", ", View.Policy.InstallerProviders);
    public string Sequence => View.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public LocalizedStatus Status => LocalizedStatus.Key("component_center.sources.status." + View.Status);
    public LocalizedStatus Trust => LocalizedStatus.Key("component_center.sources.trust." + View.Policy.Trust);
    public string Problem => View.ProblemCode ?? "";
}

/// <summary>Device-local source approval. This never changes a server's runtime/Helper trust policy.</summary>
public sealed partial class CatalogSourcesViewModel : ObservableObject, IDisposable
{
    private readonly ComponentCatalogSourceStore _store;
    private ComponentCatalogSourcePolicy? _review;
    private long _expectedRevision;
    public ObservableCollection<CatalogSourceRow> Sources { get; } = [];
    [ObservableProperty] private string sourceId = "";
    [ObservableProperty] private string displayName = "";
    [ObservableProperty] private string publisherId = "";
    [ObservableProperty] private string baseUri = "";
    [ObservableProperty] private string catalogPath = "catalog.signed.json";
    [ObservableProperty] private string keyId = "";
    [ObservableProperty] private string publicKeySpki = "";
    [ObservableProperty] private string maximumValiditySeconds = "2592000";
    [ObservableProperty] private bool allowRoapp = true;
    [ObservableProperty] private bool allowApt;
    [ObservableProperty] private bool allowArchive;
    [ObservableProperty] private bool allowDownload;
    [ObservableProperty] private string privateNetwork = "";
    [ObservableProperty] private string privatePort = "443";
    [ObservableProperty] private bool isReviewing;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private LocalizedStatus notice = LocalizedStatus.Literal("");
    public string ReviewIdentity => _review is null ? "" : _review.DisplayName + " · " + _review.SourceId + " · " + _review.PublisherId;
    public string ReviewOrigin => _review is null ? "" : _review.BaseUri + _review.CatalogPath;
    public string ReviewKeys => _review is null ? "" : string.Join("\n", _review.KeyFingerprints);
    public string ReviewProviders => _review is null ? "" : string.Join(", ", _review.InstallerProviders);
    public string ReviewNetworks => _review is null ? "" : string.Join(", ", _review.PrivateNetworks.Select(grant => grant.Cidr + ":" + grant.Port));
    public string ReviewFingerprint => _review?.Fingerprint ?? "";
    public string ReviewValidity => _review is null ? "" : _review.MaximumValiditySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " s";
    public LocalizedStatus ReviewTrust => LocalizedStatus.Key("component_center.sources.trust." + (_review?.Trust ?? "user-trusted"));
    public bool CanConfirm => IsReviewing && _review is not null && !IsBusy;
    public bool CanEdit => !IsBusy && !IsReviewing;

    public CatalogSourcesViewModel(ComponentCatalogSourceStore store)
    { _store = store; store.Changed += Changed; Reload(); }

    private void Changed(object? sender, EventArgs args) => Avalonia.Threading.Dispatcher.UIThread.Post(Reload);
    public void Reload()
    {
        try { Sources.Clear(); foreach (var view in _store.Sources()) Sources.Add(new(view)); }
        catch (Exception error) when (Known(error)) { Notice = LocalizedStatus.Key("component_center.sources.state_failed"); }
    }
    private static bool Known(Exception error) => error is RuntimePolicyException or IOException or InvalidDataException
        or InvalidOperationException or ArgumentException or System.Text.Json.JsonException;

    protected override void OnPropertyChanged(PropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        if (args.PropertyName is nameof(SourceId) or nameof(DisplayName) or nameof(PublisherId) or nameof(BaseUri) or nameof(CatalogPath)
            or nameof(KeyId) or nameof(PublicKeySpki) or nameof(AllowRoapp) or nameof(AllowApt) or nameof(AllowArchive) or nameof(AllowDownload)
            or nameof(PrivateNetwork) or nameof(PrivatePort) or nameof(MaximumValiditySeconds)) ClearReview();
    }
    private void ReviewChanged()
    {
        foreach (var name in new[] { nameof(ReviewIdentity), nameof(ReviewOrigin), nameof(ReviewKeys), nameof(ReviewProviders), nameof(ReviewNetworks),
            nameof(ReviewFingerprint), nameof(ReviewValidity), nameof(ReviewTrust), nameof(CanConfirm), nameof(CanEdit) }) OnPropertyChanged(name);
        ConfirmCommand.NotifyCanExecuteChanged();
    }
    private void ClearReview() { _review = null; IsReviewing = false; ReviewChanged(); }
    partial void OnIsBusyChanged(bool value) => ReviewChanged();

    [RelayCommand]
    private void Review()
    {
        if (IsBusy) return;
        try
        {
            var providers = new List<string>();
            if (AllowRoapp) providers.Add("roapp"); if (AllowApt) providers.Add("apt");
            if (AllowArchive) providers.Add("archive"); if (AllowDownload) providers.Add("download");
            var trust = SourceId.Trim() == "relaxkonos-official-components" && PublisherId.Trim() == "relaxkon"
                && KeyId.Trim() == OfficialRuntimeCatalog.KeyId && PublicKeySpki.Trim() == OfficialRuntimeCatalog.PublicKeySpki ? "official" : "user-trusted";
            _review = new(1, SourceId.Trim(), DisplayName.Trim(), PublisherId.Trim(), trust,
                new Uri(BaseUri.Trim(), UriKind.Absolute).AbsoluteUri, CatalogPath.Trim(),
                new Dictionary<string, string>(StringComparer.Ordinal) { [KeyId.Trim()] = PublicKeySpki.Trim() }, providers.ToArray(),
                string.IsNullOrWhiteSpace(PrivateNetwork) ? [] : [new(PrivateNetwork.Trim(), int.Parse(PrivatePort, System.Globalization.CultureInfo.InvariantCulture))],
                int.Parse(MaximumValiditySeconds, System.Globalization.CultureInfo.InvariantCulture), true);
            _review.Validate(); _expectedRevision = _store.Revision;
            IsReviewing = true; Notice = LocalizedStatus.Key("component_center.sources.review_notice"); ReviewChanged();
        }
        catch (Exception error) when (Known(error) || error is FormatException or OverflowException)
        { ClearReview(); Notice = LocalizedStatus.Key("component_center.sources.invalid"); }
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (!CanConfirm || _review is not { } policy) return;
        try { _store.Approve(policy, _expectedRevision, policy.Fingerprint); ClearReview(); Notice = LocalizedStatus.Key("component_center.sources.approved"); }
        catch (Exception error) when (Known(error)) { Notice = LocalizedStatus.Key("component_center.sources.approval_failed"); }
    }
    [RelayCommand] private void CancelReview() => ClearReview();
    [RelayCommand]
    private void Edit(CatalogSourceRow? row)
    {
        if (row is null || IsBusy) return;
        var policy = row.View.Policy;
        SourceId = policy.SourceId; DisplayName = policy.DisplayName; PublisherId = policy.PublisherId;
        BaseUri = policy.BaseUri; CatalogPath = policy.CatalogPath;
        MaximumValiditySeconds = policy.MaximumValiditySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        KeyId = policy.PublicKeysSpki.Keys.First(); PublicKeySpki = policy.PublicKeysSpki[KeyId];
        AllowRoapp = policy.InstallerProviders.Contains("roapp"); AllowApt = policy.InstallerProviders.Contains("apt");
        AllowArchive = policy.InstallerProviders.Contains("archive"); AllowDownload = policy.InstallerProviders.Contains("download");
        PrivateNetwork = policy.PrivateNetworks.FirstOrDefault()?.Cidr ?? "";
        PrivatePort = (policy.PrivateNetworks.FirstOrDefault()?.Port ?? 443).ToString(System.Globalization.CultureInfo.InvariantCulture);
        // The UI edits one key/network at a time. Additional deployment-provisioned entries cannot be silently discarded.
        if (policy.PublicKeysSpki.Count != 1 || policy.PrivateNetworks.Length > 1)
        { ClearReview(); Notice = LocalizedStatus.Key("component_center.sources.complex_policy"); return; }
        Review();
    }
    [RelayCommand]
    private void Disable(CatalogSourceRow? row)
    {
        if (row is null || IsBusy) return;
        try { _store.SetEnabled(row.Id, false, _store.Revision); Notice = LocalizedStatus.Key("component_center.sources.disabled"); }
        catch (Exception error) when (Known(error)) { Notice = LocalizedStatus.Key("component_center.sources.approval_failed"); }
    }
    [RelayCommand]
    private void Remove(CatalogSourceRow? row)
    {
        if (row is null || IsBusy) return;
        try { _store.Remove(row.Id, _store.Revision); Notice = LocalizedStatus.Key("component_center.sources.removed"); }
        catch (Exception error) when (Known(error)) { Notice = LocalizedStatus.Key("component_center.sources.approval_failed"); }
    }
    [RelayCommand]
    private async Task RefreshAsync(CatalogSourceRow? row)
    {
        if (row is null || IsBusy) return;
        IsBusy = true;
        try { await _store.RefreshAsync(row.Id); Notice = LocalizedStatus.Key("component_center.sources.refreshed"); }
        catch (Exception error) when (Known(error) || error is HttpRequestException or OperationCanceledException)
        { Notice = LocalizedStatus.Key("component_center.sources.refresh_failed"); }
        finally { IsBusy = false; Reload(); }
    }
    public void Dispose() => _store.Changed -= Changed;
}
