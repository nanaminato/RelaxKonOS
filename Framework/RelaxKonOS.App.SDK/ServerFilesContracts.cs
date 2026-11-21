namespace RelaxKonOS.AppSDK;

/// <summary>Permission-gated server file access for package applications.</summary>
public interface IServerFiles
{
    /// <summary>Whether the file source uses the current SSH desktop session.</summary>
    bool IsSshSession { get; }

    /// <summary>Whether paths on the captured remote file source are compared case-sensitively.</summary>
    bool IsPathCaseSensitive { get; }

    /// <summary>
    /// Opens a server file for reading. The caller owns and must dispose <see cref="ServerFileReadResult.Content"/>
    /// when the status is <see cref="AppCapabilityResult.Succeeded"/>. In an SSH desktop the path must be the
    /// one supplied by the host to <see cref="IExternalFileOpenApplication.OpenFileAsync"/>.
    /// </summary>
    Task<ServerFileReadResult> OpenReadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Reads the contents of an editable file through the RelaxKonOS Server file API.</summary>
    Task<ServerFileContentResult> ReadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Lists the immediate children of a directory through the captured server session and read grant.</summary>
    Task<ServerDirectoryResult> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Writes a file using the current RelaxKonOS Server session and the app's write grant.</summary>
    Task<AppCapabilityResult> WriteAsync(string path, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>Uses the version from a read or prior save. A null version requests exclusive creation.</summary>
    Task<ServerFileWriteResult> WriteConditionalAsync(string path, byte[] content, string? expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a directory using the app's write grant.</summary>
    Task<AppCapabilityResult> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Renames a file or directory in place using the app's write grant and returns its server path.</summary>
    Task<ServerFileRenameResult> RenameAsync(string path, string newName, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file or directory using the app's write grant and the server's deletion policy.</summary>
    Task<AppCapabilityResult> DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Opens the host's remote file or folder picker. Save mode requires both read and write grants.</summary>
    Task<ServerFilePickResult> PickAsync(IExternalAppWindowHandle owner, ServerFilePickerOptions options);
}

public sealed record ServerFileReadResult(AppCapabilityResult Status, Stream? Content, string? FileName);
public sealed record ServerFileContentResult(AppCapabilityResult Status, byte[]? Content = null, string? Version = null);
public sealed record ServerFileWriteResult(AppCapabilityResult Status, string? Version = null);
public sealed record ServerDirectoryResult(AppCapabilityResult Status, ServerDirectory? Directory = null);
public sealed record ServerDirectory(string Path, string Name, IReadOnlyList<ServerDirectoryEntry> Entries);
public sealed record ServerDirectoryEntry(string Path, string Name, bool IsDirectory);
public sealed record ServerFileRenameResult(AppCapabilityResult Status, string? Path = null);

public enum ServerFilePickerMode { OpenFile, SelectFolder, SaveFile }

public sealed record ServerFilePickerFilter(string Label, IReadOnlyList<string> Patterns, bool IncludeExtensionlessFiles = false);

public sealed record ServerFilePickerOptions(string Title, ServerFilePickerMode Mode,
    IReadOnlyList<ServerFilePickerFilter>? Filters = null, string? SuggestedFileName = null);

/// <summary>A successful result with a null path means the user cancelled.</summary>
public sealed record ServerFilePickResult(AppCapabilityResult Status, string? Path = null);
