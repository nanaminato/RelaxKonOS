using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.Explorer.Models;

/// <summary>Window-local location and navigation state. File operations remain window-owned.</summary>
public sealed partial class ExplorerTab : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(IsComputer))]
    [NotifyPropertyChangedFor(nameof(IconKind))]
    private string? _path;

    public bool IsComputer => Path is null;

    /// <summary>
    /// The tab's location mark, drawn in the same vocabulary as the navigation tree: the computer
    /// when the tab has no location, the drive when the tab *is* the root of a location, a folder
    /// otherwise. "Is a root" is exactly "owns no parent", which is what the breadcrumb's first
    /// entry already encodes for every platform the client can talk to.
    /// </summary>
    public TreeNodeIconKind IconKind =>
        Path is null ? TreeNodeIconKind.Computer
        : Path.Length == 0 ? TreeNodeIconKind.Folder
        : ExplorerBreadcrumb.FromPath(Path).Count <= 1 ? TreeNodeIconKind.Drive
        : TreeNodeIconKind.Folder;

    public string Title => Path is null ? LocalizedText.Get("explorer.computer")
        : Path.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault() is { Length: > 0 } name ? name : Path;
    internal List<string?> History { get; } = [];
    internal int HistoryIndex { get; set; } = -1;
    internal string SearchText { get; set; } = string.Empty;
    internal ExplorerViewPreferences? Preferences { get; set; }
}
