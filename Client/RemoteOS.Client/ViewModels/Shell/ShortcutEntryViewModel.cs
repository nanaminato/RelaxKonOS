using Client.Services.VirtualSystemDrive;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Core.VirtualSystemDrive;

namespace Client.ViewModels.Shell;

/// <summary>VSD shortcut presentation; target interpretation remains in ShortcutActivationRouter.</summary>
public partial class ShortcutEntryViewModel : ObservableObject
{
    private readonly ShortcutActivationRouter _router;

    public ShortcutEntryViewModel(RemoteOsShortcut shortcut, ShortcutActivationRouter router)
    {
        Shortcut = shortcut;
        _router = router;
    }

    public RemoteOsShortcut Shortcut { get; }
    public string DisplayName => Shortcut.DisplayName;
    public string? IconGlyph => Shortcut.Icon?.Glyph ?? "↗";
    [ObservableProperty] private bool _isDesktopSelected;
    [ObservableProperty] private string? _lastProblemCode;

    [RelayCommand]
    private async Task ActivateAsync()
    {
        var result = await _router.ActivateAsync(Shortcut);
        LastProblemCode = result.Succeeded ? null : result.ProblemCode;
    }
}
