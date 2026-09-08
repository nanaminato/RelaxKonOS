using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.ViewModels.Shell;

/// <summary>
/// A single taskbar button representing every window owned by one application.
/// When an application has more than one window, its windows are exposed as preview
/// cards so the shell can activate or close a specific instance.
/// </summary>
public sealed partial class TaskbarGroupViewModel : ObservableObject
{
    public TaskbarGroupViewModel(
        AppId appId,
        string displayName,
        IEnumerable<ManagedWindow> windows)
    {
        AppId = appId;
        DisplayName = displayName;
        Update(windows);
    }

    public AppId AppId { get; }
    public string DisplayName { get; }
    public ObservableCollection<ManagedWindow> Windows { get; } = new();

    public int WindowCount => Windows.Count;
    public bool HasMultipleWindows => WindowCount > 1;
    public bool IsActive => Windows.Any(window => window.IsActive);
    public string? IconGlyph => Windows.FirstOrDefault()?.IconGlyph;
    public IImage? IconImage => Windows.FirstOrDefault()?.IconImage;
    public bool HasIconImage => IconImage is not null;

    public void Update(IEnumerable<ManagedWindow> windows)
    {
        Windows.Clear();
        foreach (var window in windows)
            Windows.Add(window);

        OnPropertyChanged(nameof(WindowCount));
        OnPropertyChanged(nameof(HasMultipleWindows));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IconGlyph));
        OnPropertyChanged(nameof(IconImage));
        OnPropertyChanged(nameof(HasIconImage));
    }
}
