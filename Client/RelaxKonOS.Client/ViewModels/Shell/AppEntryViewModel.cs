using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Runtime;
using Avalonia.Media;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.ViewModels.Shell;

/// <summary>A launchable application entry shown on the desktop and in the start menu.</summary>
public partial class AppEntryViewModel : ObservableObject, IDisposable
{
    private readonly ApplicationManager _applications;
    private readonly string? _iconPath;
    private readonly string _version;
    private bool _disposed;

    public AppEntryViewModel(ApplicationInfo info, ApplicationManager applications)
    {
        _applications = applications;
        _iconPath = info.IconPath;
        _version = info.Version;
        Id = info.Id;
        DisplayName = info.DisplayName;
        IconGlyph = info.IconGlyph;
        IconImage = AppIconImageLoader.Load(info.IconPath);
        Description = info.Description;
    }

    public AppId Id { get; }
    public string DisplayName { get; }
    public string? IconGlyph { get; }
    public IImage? IconImage { get; }
    public bool HasIconImage => IconImage is not null;
    public string? Description { get; }
    private ApplicationAvailability? _availability;
    /// <summary>
    /// Launch readiness is enforced for every application the catalog relates to a component. An
    /// application whose component the connected host cannot provide at all is ready without a badge
    /// instead of looking broken.
    /// </summary>
    public bool HasComponentDependency => ComponentCatalog.LaunchGate(Id.Value) is not null;
    public bool IsReady => !HasComponentDependency || _availability?.CanOpen == true;
    public bool HasAvailabilityBadge => HasComponentDependency && _availability?.State != ComponentAvailabilityKind.NotRequired;
    public string? AvailabilityState => HasComponentDependency ? (_availability?.State ?? ComponentAvailabilityKind.Unknown).ToString() : null;
    public LocalizedStatus AvailabilityText => HasComponentDependency
        ? LocalizedStatus.Key("component_center.badge." + (_availability?.State ?? ComponentAvailabilityKind.Unknown))
        : LocalizedStatus.Literal("");
    public void RefreshAvailability(ApplicationAvailability? availability)
    {
        if (_disposed) return;
        _availability = availability;
        OnPropertyChanged(nameof(IsReady)); OnPropertyChanged(nameof(HasAvailabilityBadge));
        OnPropertyChanged(nameof(AvailabilityText)); OnPropertyChanged(nameof(AvailabilityState));
    }
    [ObservableProperty] private bool _isDesktopSelected;

    internal bool Matches(ApplicationInfo info) => !_disposed && Id == info.Id && DisplayName == info.DisplayName
        && Description == info.Description && IconGlyph == info.IconGlyph && _iconPath == info.IconPath && _version == info.Version;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        (IconImage as IDisposable)?.Dispose();
    }

    [RelayCommand]
    private void Launch()
    {
        _applications.Launch(Id);
    }
}
