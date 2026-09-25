using System.Text;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Server.Files;

/// <summary>
/// The single place that decides whether a client-supplied name may become a path component, and the
/// single place that builds/parses the staging file name.
/// </summary>
/// <remarks>
/// A file name arrives from a multipart Content-Disposition header or from a phone's document picker.
/// Both are chosen by whoever is uploading, so the name is untrusted input: <c>Path.Combine(dir, name)</c>
/// on <c>"..\\..\\x"</c> leaves the target directory entirely. The rules below are deliberately those of
/// the strictest host (Windows) even when the server runs on Linux, because the destination of a name is
/// a host path that a different client may later resolve: a name that is legal on Linux and illegal on
/// Windows is a file the operator cannot reach.
/// </remarks>
public static class FileUploadNamePolicy
{
    /// <summary>Maximum length in UTF-16 units. NTFS allows 255 characters, so this is the ceiling.</summary>
    public const int MaximumNameLength = 255;

    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// True when <paramref name="fileName"/> is exactly one file-name component that is safe to combine
    /// with a directory.
    /// </summary>
    public static bool IsValidFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        if (fileName.Length > MaximumNameLength) return false;
        if (fileName is "." or "..") return false;
        // A trailing dot or space is silently dropped by Windows, so the file that appears on disk would
        // not be the file the user named. A trailing colon addresses an alternate data stream.
        if (fileName[^1] is '.' or ' ' or ':') return false;
        foreach (var character in fileName)
        {
            // Separators would make this more than one component; NUL and other control characters cannot
            // travel through a header or a JSON string intact.
            if (character is '/' or '\\' or '\0' || char.IsControl(character)) return false;
            if (character is ':' or '*' or '?' or '"' or '<' or '>' or '|') return false;
        }

        return !IsReservedDeviceName(fileName);
    }

    /// <summary>
    /// Reserved device names are reserved with or without an extension and regardless of case, so both
    /// <c>NUL</c> and <c>nul.txt</c> address a device instead of a file.
    /// </summary>
    private static bool IsReservedDeviceName(string fileName)
    {
        var stem = fileName;
        var extension = fileName.LastIndexOf('.');
        if (extension > 0) stem = fileName[..extension];
        stem = stem.TrimEnd(' ', '.');
        foreach (var reserved in ReservedDeviceNames)
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Name of the staging file that lives in the target directory while a session is receiving bytes.
    /// A leading dot hides it on Unix-like hosts; the destination name keeps it readable for an operator;
    /// the session id makes it attributable to exactly one session.
    /// </summary>
    public static string BuildStagingFileName(string fileName, string sessionId)
        => $".{fileName}.{sessionId}{FileUploadProtocol.StagingExtension}";

    /// <summary>
    /// Reads the session id out of a staging file name. Used to decide whether a file found in the
    /// destination directory is ours: cleanup only ever touches files whose name parses this way
    /// <em>and</em> whose session appears in the index, never a file that merely looks temporary.
    /// </summary>
    public static bool TryParseStagingFileName(string? fileName, out string sessionId)
    {
        sessionId = string.Empty;
        if (string.IsNullOrEmpty(fileName) || fileName[0] != '.') return false;
        if (!fileName.EndsWith(FileUploadProtocol.StagingExtension, StringComparison.Ordinal)) return false;
        var body = fileName.AsSpan(1, fileName.Length - 1 - FileUploadProtocol.StagingExtension.Length);
        if (body.Length <= FileUploadProtocol.SessionIdHexLength) return false;
        // The name is ".{destinationName}.{sessionId}"; the id is the last dot-separated segment.
        var dot = body.LastIndexOf('.');
        if (dot < 0) return false;
        var candidate = body[(dot + 1)..];
        if (candidate.Length != FileUploadProtocol.SessionIdHexLength) return false;
        foreach (var character in candidate)
            if (!Uri.IsHexDigit(character) || char.IsUpper(character))
                return false;
        sessionId = candidate.ToString();
        return true;
    }

    /// <summary>
    /// Path-safe form of the name as recorded in an index or an audit line: an unparseable name is
    /// replaced rather than written verbatim.
    /// </summary>
    public static string DescribeForLog(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "(none)";
        var builder = new StringBuilder(Math.Min(fileName.Length, 64));
        foreach (var character in fileName)
        {
            if (builder.Length == 64) { builder.Append('…'); break; }
            builder.Append(char.IsControl(character) ? '?' : character);
        }
        return builder.ToString();
    }
}
