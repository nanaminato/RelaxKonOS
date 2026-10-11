using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.Explorer.Models;

/// <summary>Window-local location and navigation state. File operations remain window-owned.</summary>
public sealed partial class ExplorerTab : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(IsComputer))]
    private string? _path;

    public bool IsComputer => Path is null;
    public string Title => Path is null ? LocalizedText.Get("explorer.computer")
        : Path.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault() is { Length: > 0 } name ? name : Path;
    internal List<string?> History { get; } = [];
    internal int HistoryIndex { get; set; } = -1;
    internal string SearchText { get; set; } = string.Empty;
    internal ExplorerViewPreferences? Preferences { get; set; }
}
