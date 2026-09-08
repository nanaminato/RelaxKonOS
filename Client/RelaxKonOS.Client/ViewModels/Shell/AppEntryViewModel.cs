using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Runtime;
using Avalonia.Media;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.ViewModels.Shell;

/// <summary>A launchable application entry shown on the desktop and in the start menu.</summary>
public partial class AppEntryViewModel : ObservableObject
{
    private readonly ApplicationManager _applications;

    public AppEntryViewModel(ApplicationInfo info, ApplicationManager applications)
    {
        _applications = applications;
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
    [ObservableProperty] private bool _isDesktopSelected;

    [RelayCommand]
    private void Launch()
    {
        _applications.Launch(Id);
    }
}
