namespace RelaxKonOS.Protocol.Privileged;

/// <summary>Closed file verbs a module-declared root may request. A module never supplies an executable,
/// argv, owner name, environment or a path outside its declared root.</summary>
public enum ModuleFileAction
{
    Write,
    Delete,
}

/// <summary>
/// Frozen observation of one declared configuration file. It is produced by the Helper from the host's
/// own filesystem, never by the module. A write plan must find the same state again before it runs.
/// </summary>
public sealed record ModuleFileState(
    bool Exists,
    bool IsDirectory,
    bool IsSymbolicLink,
    string Sha256,
    long Bytes,
    uint Owner,
    uint Group,
    int Mode);

/// <summary>Read-only question about one path inside one Host-approved module file root.</summary>
public sealed record ModuleFileQuery(string Root, string Path, ModuleFileAction Action);

/// <summary>
/// One exact configuration publication. The content is the bytes the user confirmed by digest; the
/// Helper writes them beside the destination and renames atomically. No path outside <see cref="Root"/>,
/// no owner name and no mode expression are accepted from the module.
/// </summary>
public sealed record ModuleFileTransaction(
    ModuleFileAction Action,
    string Root,
    string Path,
    ModuleFileState Expected,
    string ContentSha256,
    long ContentBytes,
    string? ContentBase64,
    int Mode);

/// <summary>Closed data-lifecycle verbs. Restore waits for its own recovery design and stays refused.</summary>
public enum ModuleDataAction
{
    Preserve,
    Backup,
    Delete,
}

/// <summary>
/// Frozen structural digest of one declared data root: regular-file relative name, length and content
/// SHA-256, in ordinal order. Symbolic links and special files make the root unusable.
/// </summary>
public sealed record ModuleDataState(bool Exists, bool IsDirectory, long Files, long Bytes, string Digest);

/// <summary>Read-only question about one exact declared data root.</summary>
public sealed record ModuleDataQuery(string Root, ModuleDataAction Action);

/// <summary>
/// One exact data-lifecycle action. <see cref="Scope"/> is the authenticated module identity the Broker
/// supplies, so one module can never read or overwrite another module's backup or preservation receipt.
/// </summary>
public sealed record ModuleDataTransaction(
    ModuleDataAction Action,
    string Root,
    ModuleDataState Expected,
    string Scope,
    string? BackupRef);

/// <summary>Bounded shape rules shared by the Host and the Helper. They describe values, not authority.</summary>
public static class ModuleResourceIdentity
{
    public static bool Identifier(string? value) => value is { Length: > 0 and <= 64 }
        && value[0] is >= 'a' and <= 'z'
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');

    /// <summary>Absolute POSIX path without empty, dot or parent components. Linux is the only delivered
    /// module file/data platform, so the Helper deliberately accepts no drive or UNC spelling.</summary>
    public static bool CanonicalPath(string? value) => value is { Length: >= 2 and <= 512 }
        && value[0] == '/' && value[^1] != '/'
        && !value.Any(char.IsControl)
        && value[1..].Split('/').All(part => part.Length is > 0 and <= 128 && part is not ("." or ".."));

    public static string Normalize(string value) => value.Replace('\\', '/').TrimEnd('/');

    public static bool WithinRoot(string root, string path)
    {
        var normalizedRoot = Normalize(root);
        var normalizedPath = Normalize(path);
        return normalizedPath.Length > normalizedRoot.Length
            && normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.Ordinal)
            && CanonicalPath(normalizedPath);
    }

    public static bool IsRoot(string root, string value) =>
        CanonicalPath(root) && CanonicalPath(value) && Normalize(root) == Normalize(value);

    public static bool Usable(ModuleFileState? state) => state is not null
        && state.Bytes is >= 0 and <= 1073741824
        && state.Mode is >= 0 and <= 0x1ff
        && (!state.Exists
            ? !state.IsDirectory && !state.IsSymbolicLink && state.Sha256 == "" && state.Bytes == 0 && state.Owner == 0 && state.Group == 0
            : !state.IsDirectory && !state.IsSymbolicLink && Digest(state.Sha256));

    public static bool Usable(ModuleDataState? state) => state is not null
        && state.Files is >= 0 and <= 100000
        && state.Bytes is >= 0 and <= 1073741824
        && (!state.Exists
            ? !state.IsDirectory && state.Files == 0 && state.Bytes == 0 && state.Digest == ""
            : state.IsDirectory && Digest(state.Digest));

    public static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
}
