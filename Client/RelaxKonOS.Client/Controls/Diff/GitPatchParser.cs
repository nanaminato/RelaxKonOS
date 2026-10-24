using System.Globalization;
using System.Text;

namespace RelaxKonOS.Client.Controls.Diff;

/// <summary>
/// Turns the raw patch text the server returns into a <see cref="DiffFile"/> list.
/// Mirrors <c>git diff</c> semantics rather than a grammar: header lines are metadata, hunks are
/// opened by <c>@@</c> and closed by the next header, and line numbers advance per marker.
/// </summary>
internal static class GitPatchParser
{
    private const string DiffHeader = "diff --git ";

    public static List<DiffFile> Parse(string patch)
    {
        var files = new List<DiffFile>();
        var lines = SplitLines(patch);
        DiffFile? file = null;
        DiffHunk? hunk = null;
        var oldNumber = 0;
        var newNumber = 0;

        foreach (var raw in lines)
        {
            if (raw.StartsWith(DiffHeader, StringComparison.Ordinal))
            {
                Close(hunk, oldNumber, newNumber);
                hunk = null;
                file = new DiffFile();
                files.Add(file);
                var (oldPath, newPath) = ParseDiffHeaderPaths(raw);
                file.OldPath = oldPath;
                file.NewPath = newPath;
                Append(file, raw, DiffLineKind.Metadata);
                continue;
            }

            if (raw.StartsWith("@@", StringComparison.Ordinal) && TryParseHunkHeader(raw, out var header))
            {
                Close(hunk, oldNumber, newNumber);
                file ??= AddImplicitFile(files);
                var headerLineCount = file.RawLines.Count;
                var headerLineIndex = file.Lines.Count;
                Append(file, raw, DiffLineKind.HunkHeader);
                hunk = new DiffHunk(file.Hunks.Count, header.OldStart, header.OldCount, header.NewStart, header.NewCount, header.Section, headerLineIndex);
                file.Hunks.Add(hunk);
                if (file.Hunks.Count == 1)
                    file.HeaderLineCount = headerLineCount;
                oldNumber = header.OldStart;
                newNumber = header.NewStart;
                continue;
            }

            if (file is null)
            {
                // A patch may start straight at `--- a/x` (git show --format=) or at a hunk in tests.
                if (raw.Length == 0)
                    continue;
                file = AddImplicitFile(files);
            }

            if (hunk is not null && raw.Length > 0)
            {
                switch (raw[0])
                {
                    case '+':
                        hunk.Lines.Add(Append(file, raw, DiffLineKind.Added, null, newNumber));
                        newNumber++;
                        file.Additions++;
                        continue;
                    case '-':
                        hunk.Lines.Add(Append(file, raw, DiffLineKind.Removed, oldNumber, null));
                        oldNumber++;
                        file.Deletions++;
                        continue;
                    case ' ':
                        hunk.Lines.Add(Append(file, raw, DiffLineKind.Context, oldNumber, newNumber));
                        oldNumber++;
                        newNumber++;
                        continue;
                    case '\\':
                        hunk.Lines.Add(Append(file, raw, DiffLineKind.NoNewline));
                        continue;
                    default:
                        // Anything else ends the hunk; git emits the next file header or a binary notice.
                        Close(hunk, oldNumber, newNumber);
                        hunk = null;
                        break;
                }
            }

            if (raw.Length == 0)
            {
                Close(hunk, oldNumber, newNumber);
                hunk = null;
                continue;
            }

            Append(file, raw, DiffLineKind.Metadata);
            ApplyHeader(file, raw);
        }

        Close(hunk, oldNumber, newNumber);
        if (file is not null)
            FinalizeKind(file);
        return files;
    }

    private static DiffFile AddImplicitFile(List<DiffFile> files)
    {
        var file = new DiffFile();
        files.Add(file);
        return file;
    }

    private static DiffLine Append(DiffFile file, string raw, DiffLineKind kind, int? oldNumber = null, int? newNumber = null)
    {
        var index = file.RawLines.Count;
        file.RawLines.Add(raw);
        var text = kind switch
        {
            DiffLineKind.Context or DiffLineKind.Added or DiffLineKind.Removed => raw.Length > 1 ? raw[1..] : string.Empty,
            DiffLineKind.NoNewline => raw.Length > 2 ? raw[2..] : raw,
            _ => raw,
        };
        var line = new DiffLine(kind, text, raw, oldNumber, newNumber, index);
        file.Lines.Add(line);
        return line;
    }

    /// <summary>Marks a hunk short when its body does not reach the counts its header promised.</summary>
    private static void Close(DiffHunk? hunk, int oldNumber, int newNumber)
    {
        if (hunk is null)
            return;
        hunk.IsComplete = oldNumber - hunk.OldStart == hunk.OldCount
            && newNumber - hunk.NewStart == hunk.NewCount;
    }

    private static void ApplyHeader(DiffFile file, string raw)
    {
        if (TryHeaderValue(raw, "--- ", out var oldSide))
        {
            var path = ParseSidePath(oldSide);
            file.OldPath = path;
            file.OldMissing = path is null;
            return;
        }

        if (TryHeaderValue(raw, "+++ ", out var newSide))
        {
            var path = ParseSidePath(newSide);
            file.NewPath = path;
            file.NewMissing = path is null;
            return;
        }

        if (TryHeaderValue(raw, "rename from ", out var renamedFrom))
        {
            file.OldPath = Unquote(renamedFrom);
            file.Renamed = true;
            return;
        }

        if (TryHeaderValue(raw, "rename to ", out var renamedTo))
        {
            file.NewPath = Unquote(renamedTo);
            file.Renamed = true;
            return;
        }

        if (TryHeaderValue(raw, "copy from ", out var copiedFrom))
        {
            file.OldPath = Unquote(copiedFrom);
            file.Copied = true;
            return;
        }

        if (TryHeaderValue(raw, "copy to ", out var copiedTo))
        {
            file.NewPath = Unquote(copiedTo);
            file.Copied = true;
            return;
        }

        if (raw.StartsWith("Binary files ", StringComparison.Ordinal) || raw == "GIT binary patch")
            file.IsBinary = true;
    }

    private static void FinalizeKind(DiffFile file)
    {
        if (file.HeaderLineCount == 0)
            file.HeaderLineCount = file.RawLines.Count;

        file.Kind = file.IsBinary
            ? DiffFileKind.Binary
            : file.OldMissing
                ? DiffFileKind.Added
                : file.NewMissing
                    ? DiffFileKind.Deleted
                    : file.Copied
                        ? DiffFileKind.Copied
                        : file.Renamed
                            ? DiffFileKind.Renamed
                            : DiffFileKind.Modified;
    }

    private static bool TryHeaderValue(string raw, string prefix, out string value)
    {
        if (!raw.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        value = raw[prefix.Length..].Trim();
        return true;
    }

    private readonly record struct HunkHeader(int OldStart, int OldCount, int NewStart, int NewCount, string Section);

    private static bool TryParseHunkHeader(string raw, out HunkHeader header)
    {
        header = default;
        var close = raw.IndexOf("@@", 2, StringComparison.Ordinal);
        if (close < 0)
            return false;

        var ranges = raw[2..close].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (ranges.Length < 2)
            return false;

        if (!TryParseRange(ranges[0], '-', out var oldStart, out var oldCount))
            return false;
        if (!TryParseRange(ranges[1], '+', out var newStart, out var newCount))
            return false;

        var section = raw.Length > close + 2 ? raw[(close + 2)..].Trim() : string.Empty;
        header = new HunkHeader(oldStart, oldCount, newStart, newCount, section);
        return true;
    }

    /// <summary>Reads <c>-14,3</c>; an omitted count means one line, as git writes it.</summary>
    private static bool TryParseRange(string value, char sign, out int start, out int count)
    {
        start = 0;
        count = 0;
        if (value.Length < 2 || value[0] != sign)
            return false;

        var body = value[1..];
        var comma = body.IndexOf(',');
        var startText = comma < 0 ? body : body[..comma];
        var countText = comma < 0 ? "1" : body[(comma + 1)..];
        return int.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out start)
            && int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out count);
    }

    private static (string? OldPath, string? NewPath) ParseDiffHeaderPaths(string raw)
    {
        var body = raw[DiffHeader.Length..].Trim();
        var first = ReadToken(body, 0, out var firstEnd);
        if (firstEnd <= 0 || first.Length == 0)
            return (null, null);

        var secondStart = firstEnd;
        while (secondStart < body.Length && body[secondStart] == ' ')
            secondStart++;
        if (secondStart >= body.Length)
            return (Unquote(first), null);

        var second = ReadToken(body, secondStart, out _);
        return (NormalizePath(first), NormalizePath(second));
    }

    /// <summary>Reads a path token, honouring git's C-style quotes around names with special bytes.</summary>
    private static string ReadToken(string value, int start, out int end)
    {
        if (start >= value.Length || value[start] != '"')
        {
            var space = value.IndexOf(' ', start);
            end = space < 0 ? value.Length : space;
            return value[start..end];
        }

        var index = start + 1;
        while (index < value.Length)
        {
            if (value[index] == '\\' && index + 1 < value.Length)
            {
                index += 2;
                continue;
            }

            if (value[index] == '"')
            {
                index++;
                break;
            }

            index++;
        }

        end = Math.Min(index, value.Length);
        return value[start..end];
    }

    /// <summary>A side path of <c>---</c>/<c>+++</c>: null for /dev/null, prefix stripped.</summary>
    private static string? ParseSidePath(string value)
    {
        if (value == "/dev/null")
            return null;
        var path = NormalizePath(value);
        return path.Length == 0 ? null : path;
    }

    private static string NormalizePath(string value)
    {
        var path = Unquote(value.Trim());
        if (path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal))
            path = path[2..];
        return path;
    }

    /// <summary>
    /// Decodes git's quoted paths: octal escapes are UTF-8 bytes, so they are accumulated as bytes
    /// and decoded once instead of being turned into chars one at a time.
    /// </summary>
    private static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            return value;

        var inner = value[1..^1];
        var bytes = new List<byte>(inner.Length);
        var literalStart = 0;
        var index = 0;
        void FlushLiteral(int end)
        {
            if (end > literalStart)
                bytes.AddRange(Encoding.UTF8.GetBytes(inner[literalStart..end]));
        }

        while (index < inner.Length)
        {
            if (inner[index] != '\\' || index + 1 >= inner.Length)
            {
                index++;
                continue;
            }

            FlushLiteral(index);
            index++;
            var escape = inner[index];
            index++;
            switch (escape)
            {
                case >= '0' and <= '7':
                    var code = escape - '0';
                    var digits = 1;
                    while (digits < 3 && index < inner.Length && inner[index] >= '0' && inner[index] <= '7')
                    {
                        code = code * 8 + (inner[index] - '0');
                        index++;
                        digits++;
                    }

                    bytes.Add((byte)code);
                    break;
                case 'a': bytes.Add(0x07); break;
                case 'b': bytes.Add(0x08); break;
                case 'f': bytes.Add(0x0C); break;
                case 'n': bytes.Add(0x0A); break;
                case 'r': bytes.Add(0x0D); break;
                case 't': bytes.Add(0x09); break;
                case 'v': bytes.Add(0x0B); break;
                default: bytes.AddRange(Encoding.UTF8.GetBytes(escape.ToString())); break;
            }

            literalStart = index;
        }

        FlushLiteral(inner.Length);
        return Encoding.UTF8.GetString([.. bytes]);
    }

    private static List<string> SplitLines(string patch)
    {
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < patch.Length; index++)
        {
            if (patch[index] != '\n')
                continue;
            var end = index > start && patch[index - 1] == '\r' ? index - 1 : index;
            lines.Add(patch[start..end]);
            start = index + 1;
        }

        if (start < patch.Length)
        {
            var end = patch.Length;
            if (end > start && patch[end - 1] == '\r')
                end--;
            lines.Add(patch[start..end]);
        }

        return lines;
    }
}
