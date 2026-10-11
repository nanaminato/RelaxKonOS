using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Client.Apps.ImageViewer;
using RelaxKonOS.Client.Apps.TextEditor;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.CodeEditor;

internal static class CodeEditorFileContent
{
    public static bool IsImage(string path) => ImageViewerApp.SupportedExtensions.Contains(
        Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static string DetectEncoding(byte[] bytes, string fallback)
    {
        // UTF-32 must be checked before UTF-16 because their little-endian BOMs overlap.
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) return "UTF-32 LE";
        if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) return "UTF-32 BE";
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return "UTF-16 LE";
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return "UTF-16 BE";
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return "UTF-8 BOM";
        return fallback;
    }

    public static bool IsBinary(string path, string text)
    {
        var kind = ExplorerFileIconKindResolver.ForEntry(FileSystemEntryType.File, path);
        if (kind is ExplorerFileIconKind.Image or ExplorerFileIconKind.Audio or ExplorerFileIconKind.Video
            or ExplorerFileIconKind.Archive or ExplorerFileIconKind.Application or ExplorerFileIconKind.Database
            or ExplorerFileIconKind.DiskImage or ExplorerFileIconKind.Font or ExplorerFileIconKind.Pdf) return true;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".doc" or ".docx" or ".odt" or ".pages" or ".epub" or ".mobi"
            or ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".ods" or ".ppt" or ".pptx"
            or ".pps" or ".ppsx" or ".odp" or ".key" or ".dll" or ".pdb" or ".so" or ".bin") return true;
        var sample = text.AsSpan(0, Math.Min(text.Length, 8192));
        var controls = 0;
        foreach (var character in sample)
        {
            if (character == '\0') return true;
            if (char.IsControl(character) && character is not '\r' and not '\n' and not '\t' and not '\f') controls++;
        }
        return controls > sample.Length / 100;
    }
}
