namespace RelaxKonOS.Client.Controls.Diff;

/// <summary>The role a single line plays inside a unified diff.</summary>
public enum DiffLineKind
{
    /// <summary>File headers, mode/index lines, rename and binary markers.</summary>
    Metadata,
    /// <summary>The <c>@@ -a,b +c,d @@</c> line that opens a hunk.</summary>
    HunkHeader,
    /// <summary>An unchanged line shown as context.</summary>
    Context,
    /// <summary>A line present only on the new side.</summary>
    Added,
    /// <summary>A line present only on the old side.</summary>
    Removed,
    /// <summary>Git's <c>\ No newline at end of file</c> notice; it belongs to the line above.</summary>
    NoNewline,
}

/// <summary>What happened to the file as a whole, as far as the patch can tell.</summary>
public enum DiffFileKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    Binary,
}

/// <summary>
/// One display line of a patch. <see cref="Text"/> has the diff marker stripped so it can be
/// rendered or highlighted as source, while <see cref="Raw"/> keeps the exact bytes the server sent
/// so a selected subset can be replayed as a patch.
/// </summary>
/// <param name="Kind">The role of the line.</param>
/// <param name="Text">The line without its leading marker.</param>
/// <param name="Raw">The line exactly as it appears in the patch.</param>
/// <param name="OldNumber">The 1-based number on the old side, when the line exists there.</param>
/// <param name="NewNumber">The 1-based number on the new side, when the line exists there.</param>
/// <param name="PatchIndex">The index of <see cref="Raw"/> inside <see cref="DiffFile.RawLines"/>.</param>
public sealed record DiffLine(
    DiffLineKind Kind,
    string Text,
    string Raw,
    int? OldNumber,
    int? NewNumber,
    int PatchIndex);

/// <summary>
/// One hunk of a file diff. The header numbers follow git's own convention, so
/// <see cref="OldEnd"/> and <see cref="NewEnd"/> are exclusive — they are the values a rebuilt
/// header must use when only part of the hunk is replayed.
/// </summary>
public sealed class DiffHunk
{
    internal DiffHunk(int index, int oldStart, int oldCount, int newStart, int newCount, string section, int headerLineIndex)
    {
        Index = index;
        OldStart = oldStart;
        OldCount = oldCount;
        NewStart = newStart;
        NewCount = newCount;
        Section = section;
        HeaderLineIndex = headerLineIndex;
    }

    /// <summary>Zero-based position of the hunk within its file.</summary>
    public int Index { get; }

    public int OldStart { get; }
    public int OldCount { get; }
    public int NewStart { get; }
    public int NewCount { get; }

    /// <summary>The text after the closing <c>@@</c>: git's function context hint, possibly empty.</summary>
    public string Section { get; }

    /// <summary>Index of the header line inside <see cref="DiffFile.Lines"/>.</summary>
    public int HeaderLineIndex { get; }

    /// <summary>The body lines: context, added, removed and the no-newline notices.</summary>
    public List<DiffLine> Lines { get; } = [];

    /// <summary>Old side, exclusive end.</summary>
    public int OldEnd => OldStart + OldCount;

    /// <summary>New side, exclusive end.</summary>
    public int NewEnd => NewStart + NewCount;

    /// <summary>
    /// False when the body does not satisfy the header counts — the server truncates large patches,
    /// so a hunk can arrive cut in half. Rendering still works; replaying a partial hunk cannot.
    /// </summary>
    public bool IsComplete { get; internal set; } = true;

    public bool ContainsOld(int lineNumber) => lineNumber >= OldStart && lineNumber < OldEnd;

    public bool ContainsNew(int lineNumber) => lineNumber >= NewStart && lineNumber < NewEnd;
}

/// <summary>One file of a patch: its header lines, its hunks, and the raw text needed to replay it.</summary>
public sealed class DiffFile
{
    public string? OldPath { get; internal set; }
    public string? NewPath { get; internal set; }

    /// <summary>The path to show: the new side, unless the file was deleted.</summary>
    public string DisplayPath => NewPath ?? OldPath ?? string.Empty;

    public DiffFileKind Kind { get; internal set; } = DiffFileKind.Modified;

    /// <summary>Whether git reported a binary payload instead of text hunks.</summary>
    public bool IsBinary { get; internal set; }

    /// <summary>Parser bookkeeping: the old side was /dev/null, so the file is new.</summary>
    internal bool OldMissing { get; set; }

    /// <summary>Parser bookkeeping: the new side was /dev/null, so the file was deleted.</summary>
    internal bool NewMissing { get; set; }

    /// <summary>Parser bookkeeping: the patch carried rename from/to lines.</summary>
    internal bool Renamed { get; set; }

    /// <summary>Parser bookkeeping: the patch carried copy from/to lines.</summary>
    internal bool Copied { get; set; }

    /// <summary>Every display line of the file, headers included, in patch order.</summary>
    public List<DiffLine> Lines { get; } = [];

    public List<DiffHunk> Hunks { get; } = [];

    /// <summary>The file's section of the patch, exactly as received.</summary>
    public List<string> RawLines { get; } = [];

    /// <summary>How many of <see cref="RawLines"/> precede the first hunk header.</summary>
    public int HeaderLineCount { get; internal set; }

    public int Additions { get; internal set; }
    public int Deletions { get; internal set; }

    /// <summary>True when any hunk arrived cut short, e.g. by the server's patch size cap.</summary>
    public bool IsIncomplete => Hunks.Any(hunk => !hunk.IsComplete);

    /// <summary>The hunk that owns a line of the new side, if any.</summary>
    public DiffHunk? HunkAtNew(int lineNumber) => Hunks.FirstOrDefault(hunk => hunk.ContainsNew(lineNumber));

    /// <summary>The hunk that owns a line of the old side, if any.</summary>
    public DiffHunk? HunkAtOld(int lineNumber) => Hunks.FirstOrDefault(hunk => hunk.ContainsOld(lineNumber));
}

/// <summary>
/// A patch parsed once and shared by rendering, replaying a line selection and the checks.
/// Parsing never throws on malformed or truncated input: unknown lines become
/// <see cref="DiffLineKind.Metadata"/> and short hunks are marked incomplete.
/// </summary>
public sealed class DiffDocument
{
    private static readonly DiffDocument EmptyDocument = new([]);

    public DiffDocument(IReadOnlyList<DiffFile> files) => Files = files;

    public IReadOnlyList<DiffFile> Files { get; }

    /// <summary>True when there is nothing to render: no files, or only header-only files.</summary>
    public bool IsEmpty => Files.Count == 0 || Files.All(file => file.Lines.Count == 0);

    public int Additions => Files.Sum(file => file.Additions);

    public int Deletions => Files.Sum(file => file.Deletions);

    /// <summary>True when every file of the patch is binary.</summary>
    public bool IsBinary => Files.Count > 0 && Files.All(file => file.IsBinary);

    /// <summary>True when a hunk was cut short by the sender's size cap.</summary>
    public bool IsIncomplete => Files.Any(file => file.IsIncomplete);

    public static DiffDocument Parse(string? patch)
        => string.IsNullOrWhiteSpace(patch) ? EmptyDocument : new(GitPatchParser.Parse(patch));
}
