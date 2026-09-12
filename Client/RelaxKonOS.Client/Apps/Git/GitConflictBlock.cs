using System.Text.RegularExpressions;

namespace RelaxKonOS.Client.Apps.Git;

/// <summary>Recognizes complete Git merge/diff3/zdiff3 marker blocks without changing line endings.</summary>
public sealed record GitConflictBlock(int Start, int Length, string Ours, string Theirs, int Line)
{
    public string Label => $"L{Line}";
    public static IReadOnlyList<GitConflictBlock> Parse(string text)
    {
        var blocks = new List<GitConflictBlock>();
        var lines = Regex.Matches(text, @"[^\n]*\n|[^\n]+$");
        int start = -1, ours = 0, oursEnd = -1, theirs = -1, width = 0;
        foreach (Match match in lines)
        {
            var line = match.Value.TrimEnd('\r', '\n');
            int Marker(char c) => line.TakeWhile(ch => ch == c).Count();
            var opening = Marker('<');
            if (opening >= 7 && line.Length > opening && line[opening] == ' ')
            { start = match.Index; ours = match.Index + match.Length; oursEnd = theirs = -1; width = opening; continue; }
            if (start < 0) continue;
            if (Marker('|') == width && oursEnd < 0) oursEnd = match.Index;
            else if (line == new string('=', width))
            { if (oursEnd < 0) oursEnd = match.Index; theirs = match.Index + match.Length; }
            else if (Marker('>') == width && theirs >= 0)
            {
                blocks.Add(new(start, match.Index + match.Length - start, text[ours..oursEnd], text[theirs..match.Index],
                    1 + text[..start].Count(c => c == '\n')));
                start = -1;
            }
        }
        return blocks;
    }
}
