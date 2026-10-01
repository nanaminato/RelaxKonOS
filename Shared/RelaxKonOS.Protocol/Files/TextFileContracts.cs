using System.Text;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Protocol.Files;

public sealed record TextFileDto(string Path, string Content, string Version, string Encoding, bool Bom, string Newline);
public sealed record SaveTextFileRequest(string Content, string ExpectedVersion, string Encoding, bool Bom);
public sealed record CreateTextFileRequest(string Content, string Encoding, bool Bom);

/// <summary>Shared bounded, lossless text policy for file and Git editors. Content retains its line endings.</summary>
public static class TextFileCodec
{
    public const int MaximumBytes = GitTextFileWrite.MaximumBytes;

    public static async Task<byte[]> ReadBytesAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream.CanSeek && stream.Length > MaximumBytes) throw new InvalidDataException("Text file is too large.");
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + count > MaximumBytes) throw new InvalidDataException("Text file is too large.");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    public static TextFileDto Decode(string path, byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Text file is too large.");
        var (name, length) = bytes.AsSpan() switch
        {
            var b when b.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) => ("utf-32le", 4),
            var b when b.StartsWith(new byte[] { 0, 0, 0xfe, 0xff }) => ("utf-32be", 4),
            var b when b.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) => ("utf-8", 3),
            var b when b.StartsWith(new byte[] { 0xff, 0xfe }) => ("utf-16le", 2),
            var b when b.StartsWith(new byte[] { 0xfe, 0xff }) => ("utf-16be", 2),
            _ => ("utf-8", 0),
        };
        var content = GetEncoding(name).GetString(bytes, length, bytes.Length - length);
        Validate(content);
        return new(path, content, GitTextFileWrite.Version(bytes), name, length > 0, Newline(content));
    }

    public static byte[] Encode(string content, string encoding, bool bom)
    {
        Validate(content);
        if (encoding != "utf-8" && !bom) throw new ArgumentException("UTF-16 and UTF-32 require a BOM for unambiguous detection.");
        var codec = GetEncoding(encoding);
        // Validate size before allocating the encoded buffer.
        var preamble = bom ? codec.GetPreamble() : [];
        if (codec.GetByteCount(content) + preamble.Length > MaximumBytes)
            throw new InvalidDataException("Text file is too large.");
        return [.. preamble, .. codec.GetBytes(content)];
    }

    private static Encoding GetEncoding(string name) => name switch
    {
        "utf-8" => new UTF8Encoding(true, true),
        "utf-16le" => new UnicodeEncoding(false, true, true),
        "utf-16be" => new UnicodeEncoding(true, true, true),
        "utf-32le" => new UTF32Encoding(false, true, true),
        "utf-32be" => new UTF32Encoding(true, true, true),
        _ => throw new ArgumentException("Unsupported text encoding."),
    };

    private static void Validate(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Any(c => c < ' ' && c is not ('\t' or '\r' or '\n')))
            throw new InvalidDataException("Binary content cannot be edited as text.");
    }

    public static string Newline(string content)
    {
        var crlf = content.Contains("\r\n", StringComparison.Ordinal);
        var remainder = content.Replace("\r\n", "", StringComparison.Ordinal);
        var lf = remainder.Contains('\n');
        var cr = remainder.Contains('\r');
        if ((crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0) > 1) return "mixed";
        return crlf ? "crlf" : lf ? "lf" : cr ? "cr" : "none";
    }
}
