using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Protocol.AppSettings;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage.Sqlite;
using RelaxKonOS.Server.UserExecution;
using RelaxKonOS.Server.HostMode;

namespace RelaxKonOS.Server.Git;

public sealed partial class LocalGitRepositoryService
{
    private static GitStatusDto ParseStatus(string output, string configVersion)
    {
        string branch = "unknown";
        string? upstream = null;
        int ahead = 0, behind = 0;
        bool isDetached = false;
        var staged = new List<GitFileChangeDto>();
        var unstaged = new List<GitFileChangeDto>();
        var untracked = new List<GitFileChangeDto>();
        var conflicts = new List<GitFileChangeDto>();

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("# branch.head"))
            {
                branch = line["# branch.head".Length..].Trim();
                if (branch == "(detached)") { isDetached = true; branch = "HEAD (detached)"; }
            }
            else if (line.StartsWith("# branch.upstream"))
            {
                upstream = line["# branch.upstream".Length..].Trim();
                if (string.IsNullOrEmpty(upstream)) upstream = null;
            }
            else if (line.StartsWith("# branch.ab"))
            {
                var abPart = line["# branch.ab".Length..].Trim();
                var abParts = abPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (abParts.Length >= 2)
                {
                    ahead = int.TryParse(abParts[0].TrimStart('+'), out var a) ? a : 0;
                    behind = int.TryParse(abParts[1].TrimStart('-'), out var b) ? b : 0;
                }
            }
            else if (line.StartsWith("u "))
            {
                // Unmerged entry: "u XY sub mH mI mW hH hI path" — path 仍为最后一段
                var (path, _) = TakePathAfterNSpaces(line, 10);
                if (!string.IsNullOrEmpty(path))
                    conflicts.Add(new GitFileChangeDto(path, Staged: false, Status: "conflicted"));
            }
            else if (line.StartsWith("1 ") || line.StartsWith("2 "))
            {
                // Type 1: "1 XY sub mH mI mW hH hI path" — 8 个空格字段 + 路径
                // Type 2: "2 XY sub mH mI mW hH hI score path⇥orig_path" — 9 个空格字段 + 路径(可能 TAB+原始路径)
                var isRename = line[0] == '2';
                var (path, origPath) = TakePathAfterNSpaces(line, isRename ? 9 : 8);
                if (string.IsNullOrEmpty(path)) continue;

                var xyText = line.Length > 3 ? line[2..4] : "  ";
                var x = xyText[0]; // staged side
                var y = xyText[1]; // unstaged side

                var status = MapStatusChar(y != '.' && y != ' ' ? y : x);

                // X = staged status: ' ' 无、'.' 无 之外的字母表示 staged 改动
                if (x != ' ' && x != '.' && x != '?')
                    staged.Add(new GitFileChangeDto(path, Staged: true, Status: MapStatusChar(x), OldPath: origPath));
                // Y = unstaged status: ' ' 无、'.' 无
                if (y != ' ' && y != '.' && y != '?')
                    unstaged.Add(new GitFileChangeDto(path, Staged: false, Status: MapStatusChar(y), OldPath: origPath));
            }
            else if (line.StartsWith("? "))
            {
                var filePath = line[2..];
                if (!string.IsNullOrEmpty(filePath))
                {
                    // Handle quoted paths (git quotes paths with special chars)
                    if (filePath.StartsWith('"') && filePath.EndsWith('"'))
                        filePath = UnquotePath(filePath);
                    untracked.Add(new GitFileChangeDto(filePath, Staged: false, Status: "untracked"));
                }
            }
        }
        return new GitStatusDto(branch, staged, unstaged, untracked, conflicts, configVersion, upstream, ahead, behind, isDetached);
    }

    /// <summary>从 porcelain v2 状态行取最后一段路径（可能含空格）：跳过前 n 个空格分隔的字段，剩余即路径。
    /// Type 2（rename/copy）里路径字段格式为 path⇥orig_path，用 TAB 分隔；返回 (path, origPath_or_null)。</summary>
    private static (string Path, string? OrigPath) TakePathAfterNSpaces(string line, int spaceFieldCount)
    {
        var remaining = line.AsSpan();
        for (int i = 0; i < spaceFieldCount; i++)
        {
            // 跳过一个字段：直到空格
            var sp = remaining.IndexOf(' ');
            if (sp < 0) return ("", null); // 字段不足
            remaining = remaining[(sp + 1)..];
        }

        var tail = remaining.ToString();
        // Split the raw rename separator before unquoting each C-style quoted path.
        // Whitespace is part of a filename and must not be trimmed.
        var tab = tail.IndexOf('\t');
        if (tab >= 0)
        {
            var path = UnquotePath(tail[..tab]);
            var original = UnquotePath(tail[(tab + 1)..]);
            return (path, original.Length == 0 ? null : original);
        }
        return (UnquotePath(tail), null);
    }

    /// <summary>Unquote a git path that was quoted because it contains special characters.
    /// Git uses C-style quoting with backslash escapes, and (when core.quotePath=true, the default)
    /// encodes each non-ASCII byte as a 3-digit octal escape, e.g. the en-dash in "Jaya – Cross Plat.pptx"
    /// (U+2013, UTF-8 bytes E2 80 93) is emitted as "Jaya \342\200\223 Cross Plat.pptx".
    /// We therefore collect raw bytes (resolving \a \b \f \n \r \t \\ \" and \nnn octal), then decode the
    /// resulting byte array as UTF-8 — this correctly reconstructs paths with en-dash, em-dash, CJK, emoji, etc.</summary>
    private static string UnquotePath(string path)
    {
        if (!path.StartsWith('"') || !path.EndsWith('"'))
            return path;

        var inner = path[1..^1];
        var bytes = new List<byte>(inner.Length);
        int i = 0;
        while (i < inner.Length)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
            {
                var next = inner[i + 1];
                switch (next)
                {
                    case 'a': bytes.Add(0x07); i += 2; break;
                    case 'b': bytes.Add(0x08); i += 2; break;
                    case 'f': bytes.Add(0x0C); i += 2; break;
                    case 'n': bytes.Add(0x0A); i += 2; break;
                    case 'r': bytes.Add(0x0D); i += 2; break;
                    case 't': bytes.Add(0x09); i += 2; break;
                    case '\\': bytes.Add(0x5C); i += 2; break;
                    case '"': bytes.Add(0x22); i += 2; break;
                    default:
                        // 3-digit octal escape \nnn (each n in 0..7) → single byte
                        if (IsOctDigit(next) && i + 3 < inner.Length
                            && IsOctDigit(inner[i + 2]) && IsOctDigit(inner[i + 3]))
                        {
                            bytes.Add((byte)((OctValue(next) << 6) | (OctValue(inner[i + 2]) << 3) | OctValue(inner[i + 3])));
                            i += 4;
                        }
                        else
                        {
                            // Unknown escape: keep the backslash literally
                            bytes.Add(0x5C);
                            i++;
                        }
                        break;
                }
            }
            else
            {
                // Plain char: emit its UTF-8 byte sequence
                bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(new[] { inner[i] }));
                i++;
            }
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());

        static bool IsOctDigit(char c) => c >= '0' && c <= '7';
        static int OctValue(char c) => c - '0';
    }

    private static string MapStatusChar(char c) => c switch
    {
        'M' => "modified",
        'A' => "added",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        _ => "modified"
    };

    private static (int ahead, int behind) ParseTrack(string track)
    {
        if (string.IsNullOrWhiteSpace(track)) return (0, 0);
        int ahead = 0, behind = 0;
        var aheadMatch = System.Text.RegularExpressions.Regex.Match(track, @"ahead (\d+)");
        var behindMatch = System.Text.RegularExpressions.Regex.Match(track, @"behind (\d+)");
        if (aheadMatch.Success) ahead = int.Parse(aheadMatch.Groups[1].Value);
        if (behindMatch.Success) behind = int.Parse(behindMatch.Groups[1].Value);
        return (ahead, behind);
    }

    private static (int additions, int deletions) ParseNumstat(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return (0, 0);
        var parts = output.Trim().Split('\t');
        if (parts.Length < 2) return (0, 0);
        int additions = int.TryParse(parts[0], out var a) ? a : 0;
        int deletions = int.TryParse(parts[1], out var d) ? d : 0;
        return (additions, deletions);
    }

    private static IReadOnlyList<GitFileChangeDto> ParseChangedFiles(string output)
    {
        var files = new List<GitFileChangeDto>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 2) continue;
            var status = parts[0];
            var kind = MapStatusChar(status[0]);
            if ((status[0] is 'R' or 'C') && parts.Length >= 3)
                files.Add(new GitFileChangeDto(UnquotePath(parts[2]), UnquotePath(parts[1]), kind));
            else
                files.Add(new GitFileChangeDto(UnquotePath(parts[1]), Status: kind));
        }
        return files;
    }

}
