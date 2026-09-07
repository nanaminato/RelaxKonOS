using Avalonia.Controls;
using Avalonia.Media;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.Core.Windows;

namespace RemoteOS.Shell;

/// <summary>Versioned, deliberately small contract shared by the client and desktop-shell packages.</summary>
public static class ShellApi
{
    public const string Version = "1.0";
    public const string DefaultShellId = "remoteos.windows-like";

    /// <summary>Returns an explicit shell identifier, using the current built-in default only when no selection exists.</summary>
    public static string ResolveId(string? id) => string.IsNullOrWhiteSpace(id) ? DefaultShellId : id.Trim();
}

public enum ShellSourceKind { BuiltIn, ExternalPackage }

[Flags]
public enum ShellCapabilities
{
    None = 0,
    Desktop = 1,
    AppLauncher = 2,
    RunningApps = 4,
    ShellOverlays = 8,
    All = Desktop | AppLauncher | RunningApps | ShellOverlays,
}

public sealed record ShellDescriptor(string Id, string DisplayName, string Version, ShellSourceKind Source,
    ShellCapabilities Capabilities, string? PackageId = null, string? UnavailableReason = null)
{
    public bool IsAvailable => string.IsNullOrEmpty(UnavailableReason);
}

public interface IShellCatalog
{
    IReadOnlyList<ShellDescriptor> Available { get; }
    event EventHandler? Changed;
    bool TryGet(string id, out ShellDescriptor descriptor);
}

public interface IDesktopShellFactory
{
    ShellDescriptor Descriptor { get; }
    IDesktopShell Create();
}

/// <summary>A launcher owns its visual tree, but never the app, window or file-system truth.</summary>
public interface IDesktopShell : IAsyncDisposable
{
    ShellDescriptor Descriptor { get; }
    Control View { get; }
    Task InitializeAsync(ShellPresentationContext context, CancellationToken cancellationToken);
    Task ActivateAsync(CancellationToken cancellationToken);
    Task DeactivateAsync(CancellationToken cancellationToken);
}

public sealed record ShellPresentationContext(ShellStateStore State, IShellActions Actions,
    IShellOverlayService Overlays, IShellSurfaceRegistry Surfaces, ILocalizationSnapshot Localization);

/// <summary>Read-only state projection. The client owns the snapshot and external packages cannot mutate it.</summary>
public sealed class ShellStateStore
{
    private object? _snapshot;
    private ShellDesktopState? _desktop;
    public object? Snapshot => _snapshot;
    /// <summary>
    /// A package-safe projection of the current desktop.  Unlike <see cref="Snapshot"/>, this
    /// never exposes client implementation types to external shell packages.
    /// </summary>
    public ShellDesktopState? Desktop => _desktop;
    public event EventHandler? Changed;
    public void Publish(object? snapshot, ShellDesktopState? desktop = null)
    {
        _snapshot = snapshot;
        _desktop = desktop;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Publishes a refreshed package-safe desktop projection without replacing host state.</summary>
    public void PublishDesktop(ShellDesktopState desktop)
    {
        _desktop = desktop;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Read-only desktop data made available to external shell packages.</summary>
public sealed record ShellDesktopState(
    IReadOnlyList<ShellApplicationEntry> Applications,
    IReadOnlyList<ShellDesktopEntry> DesktopEntries,
    bool AreDesktopIconsVisible,
    IReadOnlyList<ShellDesktopStyleEntry>? DesktopStyles = null,
    /// <summary>
    /// The host-owned brush for the active workspace wallpaper. External shells render this
    /// value as-is so built-in presets and downloaded custom images stay consistent whenever
    /// the user changes the desktop style.
    /// </summary>
    IBrush? Wallpaper = null,
    /// <summary>
    /// Theme-resolved foreground used for desktop item labels by built-in shells. External
    /// shells may use their own presentation, but receive this value to remain theme-consistent.
    /// </summary>
    IBrush? DesktopItemLabelForeground = null);

/// <summary>A launchable application in an external shell's Start menu or application list.</summary>
public sealed record ShellApplicationEntry(AppId Id, string DisplayName, string? IconGlyph, string? Description);

public enum ShellDesktopEntryKind { Application, File, Folder, Shortcut }

/// <summary>A selectable item on the authenticated user's desktop.</summary>
public sealed record ShellDesktopEntry(
    string Id,
    string DisplayName,
    ShellDesktopEntryKind Kind,
    string? IconGlyph,
    AppId? ApplicationId = null,
    bool IsSelected = false,
    /// <summary>Host-loaded application artwork. Use this in preference to <see cref="IconGlyph"/>.</summary>
    IImage? IconImage = null)
{
    /// <summary>Whether the entry has application artwork that should replace its glyph fallback.</summary>
    public bool HasIconImage => IconImage is not null;
}

/// <summary>An installed external desktop style that can be selected from another shell.</summary>
public sealed record ShellDesktopStyleEntry(string Id, string DisplayName, string Version);

public enum SettingsRoute { Root, Personalization }
public enum DesktopEntryAction { Open, OpenWith, Copy, Cut, Paste, Delete, ShowInExplorer, Properties }

public interface IShellActions
{
    Task LaunchAsync(AppId appId, CancellationToken cancellationToken = default);
    Task ActivateDesktopStyleAsync(string shellId, CancellationToken cancellationToken = default);
    Task OpenDesktopEntryAsync(string entryId, CancellationToken cancellationToken = default);
    Task RefreshDesktopAsync(CancellationToken cancellationToken = default);
    Task PasteDesktopAsync(CancellationToken cancellationToken = default);
    void ClearDesktopSelection();
    void SelectDesktopEntry(string entryId);
    void SetDesktopIconsVisible(bool visible);
    void ShowDesktop();
    void ToggleWindowGroup(AppId appId);
    void ActivateWindow(WindowId windowId);
    void MinimizeWindow(WindowId windowId);
    void CloseWindow(WindowId windowId);
    void OpenSettings(SettingsRoute route);
    void OpenDesktopFolder();
    void OpenFileExplorer();
    void OpenTerminal();
    Task ExecuteDesktopEntryActionAsync(string entryId, DesktopEntryAction action, CancellationToken cancellationToken = default);
}

public interface IShellOverlayService
{
    Task ShowDesktopDisplaySettingsAsync(CancellationToken cancellationToken = default);
    Task<bool> ShowFirstRunDesktopSetupAsync(CancellationToken cancellationToken = default);
}

public interface ILocalizationSnapshot
{
    /// <summary>Current BCP-47 language selected for the RemoteOS workspace.</summary>
    string Language { get; }

    /// <summary>
    /// Resolves a host-owned string. External shells should use this only for host terminology;
    /// package UI strings must come from language files shipped by the package.
    /// </summary>
    string Get(string key, string fallback);

    /// <summary>Raised after the workspace language changes.</summary>
    event EventHandler<ShellLanguageChangedEventArgs>? LanguageChanged;
}

public sealed class ShellLanguageChangedEventArgs(string previousLanguage, string currentLanguage) : EventArgs
{
    public string PreviousLanguage { get; } = previousLanguage;
    public string CurrentLanguage { get; } = currentLanguage;
}

public interface IShellSurfaceRegistry
{
    void Register(ShellSurfaces surfaces);
    void UpdateWorkArea(Rect workArea);
    void Clear();
}

public sealed record ShellSurfaces(Canvas WindowHost, Canvas FullScreenWindowHost,
    Panel ShellOverlayHost, Control InputBackdrop);
