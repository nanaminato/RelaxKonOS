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
        // Focus changes refresh taskbar metadata too. Preserve existing entries so their
        // preview controls, focus and pointer capture survive an activation notification.
        var desired = windows.ToList();
        for (var index = Windows.Count - 1; index >= 0; index--)
            if (!desired.Contains(Windows[index])) Windows.RemoveAt(index);
        for (var index = 0; index < desired.Count; index++)
        {
            var current = Windows.IndexOf(desired[index]);
            if (current < 0) Windows.Insert(index, desired[index]);
            else if (current != index) Windows.Move(current, index);
        }

        OnPropertyChanged(nameof(WindowCount));
        OnPropertyChanged(nameof(HasMultipleWindows));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IconGlyph));
        OnPropertyChanged(nameof(IconImage));
        OnPropertyChanged(nameof(HasIconImage));
    }
}
