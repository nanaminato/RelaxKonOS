using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Git;

public sealed record GitConflictSnapshot(
    [property: JsonPropertyName("hash"), JsonRequired] string Hash,
    [property: JsonPropertyName("content"), JsonRequired] string? Content,
    [property: JsonPropertyName("canEdit"), JsonRequired] bool CanEdit);

/// <summary>Conflict working-tree I/O. Call only after assuming the ordinary effective OS identity.</summary>
public static class GitConflictFileAccess
{
    public const int MaximumTextBytes = 200 * 1024;
    public static bool IsRelativePath(string? value) => !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value)
        && !value.Contains('\\') && !value.Contains(':') && !value.Contains('\0')
        && !value.Split('/').Any(part => part is "" or "." or ".." || part.Equals(".git", StringComparison.OrdinalIgnoreCase));

    private static string Target(string root, string relative)
    {
        if (!Path.IsPathFullyQualified(root) || !IsRelativePath(relative)) throw new ArgumentException("Invalid conflict path.");
        var current = Path.GetFullPath(root);
        foreach (var part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            // Missing entries are permitted for modify/delete conflicts. Permission failures are not absence.
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Conflict symlinks are unsupported."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return current;
    }

    public static GitConflictSnapshot Read(string root, string relative)
    {
        var path = Target(root, relative);
        FileStream stream;
        try { stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return new("deleted", null, true); }
        catch (DirectoryNotFoundException) { return new("deleted", null, true); }
        using (stream)
        {
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (stream.Length > MaximumTextBytes) return new(hash, null, false);
            stream.Position = 0;
            using var bytes = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                if (bytes.Length + count > MaximumTextBytes) return new(hash, null, false);
                bytes.Write(buffer, 0, count);
            }
            try
            {
                var text = new UTF8Encoding(false, true).GetString(bytes.ToArray());
                return text.Contains('\0') ? new(hash, null, false) : new(hash, text, true);
            }
            catch (DecoderFallbackException) { return new(hash, null, false); }
        }
    }

    public static bool Write(string root, string relative, byte[] content, string expectedHash)
    {
        if (content.Length > MaximumTextBytes || expectedHash is null
            || expectedHash != "deleted" && (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit)))
            throw new ArgumentException("Invalid conflict write.");
        var text = new UTF8Encoding(false, true).GetString(content);
        if (text.Contains('\0') || text.Split('\n').Any(line => line.StartsWith("<<<<<<<") || line.StartsWith("=======") || line.StartsWith(">>>>>>>") || line.StartsWith("|||||||")))
            throw new ArgumentException("Invalid conflict text.");
        var path = Target(root, relative);
        if (expectedHash != "deleted") return GitTextFileWrite.ReplaceIfVersion(path, content, expectedHash);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(content); file.Flush(flushToDisk: true); return true;
        }
        catch (IOException) { return false; }
    }
}
