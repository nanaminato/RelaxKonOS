using System.Security.Cryptography;

namespace RelaxKonOS.Protocol.Git;

/// <summary>A bounded, conditional write executed under the effective host user.</summary>
public static class GitTextFileWrite
{
    public const int MaximumBytes = 256 * 1024;

    public static string Version(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>Returns false when another writer changed the file. Never creates a new file.</summary>
    public static bool ReplaceIfVersion(string path, byte[] content, string expectedVersion)
    {
        if (content.Length > MaximumBytes || expectedVersion.Length != 64
            || !expectedVersion.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid conditional text write.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (file.Length > MaximumBytes) throw new InvalidDataException("Git text file is too large.");
        using var current = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = file.Read(chunk)) > 0)
        {
            if (current.Length + count > MaximumBytes) throw new InvalidDataException("Git text file is too large.");
            current.Write(chunk, 0, count);
        }
        if (!string.Equals(Version(current.GetBuffer().AsSpan(0, (int)current.Length)), expectedVersion,
                StringComparison.OrdinalIgnoreCase))
            return false;
        file.Position = 0;
        file.Write(content);
        file.SetLength(content.Length);
        file.Flush(flushToDisk: true);
        return true;
    }
}
