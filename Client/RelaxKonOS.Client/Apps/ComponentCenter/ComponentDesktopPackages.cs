using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public partial class ComponentCenterViewModel
{
    public Func<string, Task>? ReviewDesktopPackageAsync { get; set; }
    public Func<string, string?>? DesktopInstalledVersion { get; set; }
    public bool ShowDesktopPackage => SelectedDefinition?.IsDesktop == true;
    public RelaxKonOS.ManagedRuntimes.Catalog.ComponentCompatibility DesktopCompatibility => SelectedDefinition is { IsDesktop: true } definition
        ? !ComponentCatalog.CanInstall(definition.ComponentId) && ComponentCatalog.RemoteSourceId(definition.ComponentId) is not null
            ? RelaxKonOS.ManagedRuntimes.Catalog.ComponentCompatibility.PolicyBlocked
            : RelaxKonOS.ManagedRuntimes.Catalog.CompatibilityResolver.Resolve(
            ComponentCatalog.Manifest(definition.ComponentId)!,
            RoappComponentInstallerProvider.Desktop.Os, RoappComponentInstallerProvider.Desktop.Architecture,
            target => target.Installer.Type == "roapp").Status
        : RelaxKonOS.ManagedRuntimes.Catalog.ComponentCompatibility.ProviderUnavailable;
    public LocalizedStatus DesktopCompatibilityText => LocalizedStatus.Key("component_center.desktop.compatibility." + DesktopCompatibility);
    private bool _reviewingDesktopPackage;
    public bool CanReviewDesktopPackage => ShowDesktopPackage && ReviewDesktopPackageAsync is not null && !_reviewingDesktopPackage
        && DesktopCompatibility == RelaxKonOS.ManagedRuntimes.Catalog.ComponentCompatibility.Supported
        && ComponentCatalog.CanInstall(SelectedDefinition!.ComponentId);
    public string? SelectedDesktopVersion => SelectedDefinition is { IsDesktop: true } definition
        ? DesktopInstalledVersion?.Invoke(definition.ApplicationId ?? definition.ComponentId) : null;
    public void RefreshDesktopPackages() => RefreshTree();

    [RelayCommand(CanExecute = nameof(CanReviewDesktopPackage))]
    private async Task ReviewDesktopCatalogPackageAsync()
    {
        if (!CanReviewDesktopPackage || SelectedDefinition is not { } definition) return;
        _reviewingDesktopPackage = true;
        NotifyDesktopPackages();
        try { await ReviewDesktopPackageAsync!(definition.ComponentId); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
            or RelaxKonOS.ManagedRuntimes.RuntimePolicyException or System.Text.Json.JsonException or OperationCanceledException)
        { Notice = LocalizedStatus.Key("component_center.desktop.failed"); }
        finally { _reviewingDesktopPackage = false; RefreshTree(); }
    }
    private void NotifyDesktopPackages()
    {
        OnPropertyChanged(nameof(ShowDesktopPackage));
        OnPropertyChanged(nameof(CanReviewDesktopPackage));
        OnPropertyChanged(nameof(SelectedDesktopVersion));
        OnPropertyChanged(nameof(DesktopCompatibilityText));
        OnPropertyChanged(nameof(SelectedCatalogProvenance));
        ReviewDesktopCatalogPackageCommand.NotifyCanExecuteChanged();
    }
}
