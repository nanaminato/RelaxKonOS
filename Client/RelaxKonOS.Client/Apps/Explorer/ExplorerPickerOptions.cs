namespace RelaxKonOS.Client.Apps.Explorer;

/// <summary>The action a reusable RemoteExplorer picker performs when confirmed.</summary>
public enum ExplorerPickerMode
{
    OpenFile,
    SaveFile,
    SelectFolder,
}

/// <summary>A named file-name pattern set displayed by the file-picker filter control.</summary>
public sealed record ExplorerFileFilter(string Label, IReadOnlyList<string> Patterns, bool IncludeExtensionlessFiles = false)
{
    /// <summary>Matches every file, including names without an extension.</summary>
    public static ExplorerFileFilter AllFiles { get; } = new("All files (*.*)", ["*"]);
}

/// <summary>
/// Configures a RemoteExplorer picker. Filters apply to file pickers.
/// Patterns use standard wildcard syntax, for example <c>*.txt</c> or <c>*.cs</c>.
/// </summary>
public sealed record ExplorerPickerOptions(
    ExplorerPickerMode Mode = ExplorerPickerMode.OpenFile,
    bool AllowMultiple = false,
    IReadOnlyList<ExplorerFileFilter>? Filters = null,
    string? DefaultFileName = null);
