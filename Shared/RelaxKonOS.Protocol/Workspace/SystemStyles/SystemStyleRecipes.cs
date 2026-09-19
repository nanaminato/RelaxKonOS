namespace RelaxKonOS.Protocol.Workspace.SystemStyles;

/// <summary>
/// Recipe selectors are the only structural freedom a system style gets. Every accepted value is
/// a constant declared in this file: a style manifest can never contribute an AXAML fragment, a
/// CLR type name, an assembly reference, a resource URI or an event handler. The host keeps the
/// reviewed template for every component and a recipe only chooses which review-approved variant
/// of that template is used.
/// </summary>
public static class WindowChromeRecipes
{
    public const string CaptionButtonsRight = "caption-buttons-right";
    public const string TrafficLightsLeft = "traffic-lights-left";
    public const string HeaderbarRight = "headerbar-right";

    public static IReadOnlyList<string> All { get; } = [CaptionButtonsRight, TrafficLightsLeft, HeaderbarRight];
}

/// <summary>Command-menu variants. Applies to desktop and in-app <c>ContextMenu</c> alike.</summary>
public static class ContextMenuRecipes
{
    public const string CompactCommandMenu = "compact-command-menu";
    public const string RoundedCommandMenu = "rounded-command-menu";
    public const string GnomePopoverMenu = "gnome-popover-menu";

    public static IReadOnlyList<string> All { get; } = [CompactCommandMenu, RoundedCommandMenu, GnomePopoverMenu];
}

/// <summary>Window overview / task switcher variants.</summary>
public static class TaskSwitcherRecipes
{
    public const string WindowsGrid = "windows-grid";
    public const string MacOsStrip = "macos-strip";
    public const string GnomeOverview = "gnome-overview";

    public static IReadOnlyList<string> All { get; } = [WindowsGrid, MacOsStrip, GnomeOverview];
}

/// <summary>Desktop shell chrome variants. A shell package still owns its own layout.</summary>
public static class ShellChromeRecipes
{
    public const string BottomTaskbar = "bottom-taskbar";
    public const string TopMenuPlusDock = "top-menu-plus-dock";
    public const string TopBarPlusLeftDock = "top-bar-plus-left-dock";

    public static IReadOnlyList<string> All { get; } = [BottomTaskbar, TopMenuPlusDock, TopBarPlusLeftDock];
}

/// <summary>The complete, closed set of recipe slots a system style must fill.</summary>
public static class SystemStyleRecipeKinds
{
    public const string WindowChrome = "windowChrome";
    public const string ContextMenu = "contextMenu";
    public const string TaskSwitcher = "taskSwitcher";
    public const string ShellChrome = "shellChrome";

    public static IReadOnlyList<string> All { get; } = [WindowChrome, ContextMenu, TaskSwitcher, ShellChrome];

    public static IReadOnlyList<string>? Allowed(string kind) => kind switch
    {
        WindowChrome => WindowChromeRecipes.All,
        ContextMenu => ContextMenuRecipes.All,
        TaskSwitcher => TaskSwitcherRecipes.All,
        ShellChrome => ShellChromeRecipes.All,
        _ => null,
    };
}
