using Avalonia.Controls;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.Core.Windows;

namespace RemoteOS.Shell;

/// <summary>Versioned, deliberately small contract shared by the client and desktop-shell packages.</summary>
public static class ShellApi
{
    public const int Version = 1;
    public const string DefaultShellId = "remoteos.default";
    public static readonly IReadOnlyDictionary<string, string> LegacyIds = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["remoteos"] = DefaultShellId,
        ["windows-like"] = "remoteos.windows-like",
        ["macos-like"] = "remoteos.macos-like",
        ["ubuntu-like"] = "remoteos.ubuntu-like",
    };

    public static string NormalizeId(string? id) => string.IsNullOrWhiteSpace(id)
        ? DefaultShellId : LegacyIds.TryGetValue(id.Trim(), out var normalized) ? normalized : id.Trim();
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
    public object? Snapshot => _snapshot;
    public event EventHandler? Changed;
    public void Publish(object? snapshot) { _snapshot = snapshot; Changed?.Invoke(this, EventArgs.Empty); }
}

public enum SettingsRoute { Root, Personalization }
public enum DesktopEntryAction { Open, OpenWith, Copy, Cut, Paste, Delete, ShowInExplorer, Properties }

public interface IShellActions
{
    Task LaunchAsync(AppId appId, CancellationToken cancellationToken = default);
    Task OpenDesktopEntryAsync(string entryId, CancellationToken cancellationToken = default);
    Task RefreshDesktopAsync(CancellationToken cancellationToken = default);
    void ClearDesktopSelection();
    void SelectDesktopEntry(string entryId);
    void ShowDesktop();
    void ToggleWindowGroup(AppId appId);
    void ActivateWindow(WindowId windowId);
    void MinimizeWindow(WindowId windowId);
    void CloseWindow(WindowId windowId);
    void OpenSettings(SettingsRoute route);
    Task ExecuteDesktopEntryActionAsync(string entryId, DesktopEntryAction action, CancellationToken cancellationToken = default);
}

public interface IShellOverlayService
{
    Task ShowDesktopDisplaySettingsAsync(CancellationToken cancellationToken = default);
    Task<bool> ShowFirstRunDesktopSetupAsync(CancellationToken cancellationToken = default);
}

public interface ILocalizationSnapshot
{
    string Language { get; }
    string Get(string key, string fallback);
}

public interface IShellSurfaceRegistry
{
    void Register(ShellSurfaces surfaces);
    void UpdateWorkArea(Rect workArea);
    void Clear();
}

public sealed record ShellSurfaces(Canvas WindowHost, Canvas FullScreenWindowHost,
    Panel ShellOverlayHost, Control InputBackdrop);
