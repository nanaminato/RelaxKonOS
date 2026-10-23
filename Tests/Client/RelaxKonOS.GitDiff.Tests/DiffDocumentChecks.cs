using RelaxKonOS.Client.Controls.Diff;

namespace RelaxKonOS.GitDiff.Tests;

/// <summary>
/// Checks the patch interpretation every later step depends on: hunk ranges, per-side line
/// numbers, file kind, and the exact raw text kept for replaying a line selection.
/// </summary>
internal static class DiffDocumentChecks
{
    private const string ModifiedPatch = """
diff --git a/src/map.ts b/src/map.ts
index 4bee98b..9a1c2f0 100644
--- a/src/map.ts
+++ b/src/map.ts
@@ -14,3 +14,11 @@ export async function highlightTheMatches(options) {
   if (s.visible) render(s, { color: s.color })
   }
 }
+/** 绘制每颗卫星的轨道。 */
+export async function drawEachSatellite() {
+  const satellites = await api.position()
+  for (const s of satellites) {
+    if (s.visible) render(s, { color: s.color })
+  }
+}
+// 轨道层在地图之上，透明度 0.6
""";

    private const string AddedPatch = """
diff --git a/new.md b/new.md
new file mode 100644
index 0000000..1111111
--- /dev/null
+++ b/new.md
@@ -0,0 +1,2 @@
+hello
+world
""";

    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition)
                throw new InvalidOperationException($"DiffDocument check failed: {message}");
        }

        SingleHunk(Check);
        TwoHunksWithNoNewline(Check);
        AddedFile(Check);
        DeletedFile(Check);
        RenamedFile(Check);
        BinaryFile(Check);
        TruncatedHunk(Check);
        HeaderWithoutCounts(Check);
        CrlfPatch(Check);
        MultipleFiles(Check);
        EmptyPatch(Check);
        QuotedPath(Check);
        HeaderLikeContent(Check);
        return checks;
    }

    private static void SingleHunk(Action<bool, string> check)
    {
        var document = DiffDocument.Parse(ModifiedPatch);
        check(document.Files.Count == 1, "a single-file patch must produce one file");
        var file = document.Files[0];
        check(file.DisplayPath == "src/map.ts", "the display path is the new side without the b/ prefix");
        check(file.Kind == DiffFileKind.Modified, "a patch with both sides present is a modification");
        check(file.Hunks.Count == 1, "one hunk header means one hunk");
        var hunk = file.Hunks[0];
        check(hunk.OldStart == 14 && hunk.OldCount == 3, "the old range comes from the header");
        check(hunk.NewStart == 14 && hunk.NewCount == 11, "the new range comes from the header");
        check(hunk.Section == "export async function highlightTheMatches(options) {", "the section hint after @@ is kept");
        check(hunk.IsComplete, "3 context lines plus 8 additions satisfy the header counts");
        check(document.Additions == 8 && document.Deletions == 0, "additions and deletions are counted per marker");
        check(!document.IsIncomplete && !document.IsBinary && !document.IsEmpty, "a normal patch is neither incomplete, binary nor empty");
        check(file.HeaderLineCount == 4, "four header lines precede the first hunk");

        var first = hunk.Lines[0];
        check(first.Kind == DiffLineKind.Context && first.OldNumber == 14 && first.NewNumber == 14, "context lines carry both numbers");
        var last = hunk.Lines[^1];
        check(last.Kind == DiffLineKind.Added && last.OldNumber is null && last.NewNumber == 24, "added lines carry only the new number");
        check(hunk.Lines.All(line => line.Kind != DiffLineKind.Added || line.OldNumber is null), "no added line pretends to exist on the old side");
        check(first.Text == "  if (s.visible) render(s, { color: s.color })", "the marker is stripped from the text but stays in raw");

        check(file.Lines[0].Kind == DiffLineKind.Metadata && file.Lines[4].Kind == DiffLineKind.HunkHeader, "headers stay metadata and @@ stays a hunk header");
        check(file.Lines.Count == 16 && file.RawLines.Count == 16, "4 header + 1 hunk header + 11 body lines");
        check(file.Lines[^1].PatchIndex == 15, "patch indexes are positions in the raw lines");
        check(string.Join('\n', file.RawLines) == ModifiedPatch, "raw lines round-trip to the received patch");
    }

    private static void TwoHunksWithNoNewline(Action<bool, string> check)
    {
        var document = DiffDocument.Parse("""
diff --git a/notes.txt b/notes.txt
--- a/notes.txt
+++ b/notes.txt
@@ -1,2 +1,2 @@
-old
+new
 same
@@ -10 +10,2 @@ trailing section
+extra
 tail
\ No newline at end of file
""");
        var file = document.Files[0];
        check(file.Hunks.Count == 2, "two @@ headers mean two hunks");
        check(file.Hunks[1].OldStart == 10 && file.Hunks[1].OldCount == 1, "an omitted old count means one line");
        check(file.Hunks[1].NewStart == 10 && file.Hunks[1].NewCount == 2, "the second hunk keeps its own new range");
        check(file.Hunks[1].Section == "trailing section", "the second hunk keeps its section hint");
        check(file.Hunks.All(hunk => hunk.IsComplete), "both hunks are satisfied");
        check(document.Additions == 2 && document.Deletions == 1, "totals span both hunks");

        var notice = file.Hunks[1].Lines[^1];
        check(notice.Kind == DiffLineKind.NoNewline, "the no-newline notice is its own line");
        check(notice.OldNumber is null && notice.NewNumber is null, "the notice has no line number on either side");
        check(file.Hunks[1].Lines.Count == 3, "added + context + notice");
        check(file.Hunks[1].Lines[1].NewNumber == 11, "the notice does not consume a line number");
    }

    private static void AddedFile(Action<bool, string> check)
    {
        var document = DiffDocument.Parse(AddedPatch);
        var file = document.Files[0];
        check(file.Kind == DiffFileKind.Added, "/dev/null on the old side means the file is new");
        check(file.OldPath is null && file.NewPath == "new.md", "the deleted side has no path");
        check(file.Hunks[0].OldCount == 0 && file.Hunks[0].OldStart == 0, "a new file has an empty old range");
        check(file.Hunks[0].Lines.All(line => line.OldNumber is null), "no line of a new file has an old number");
        check(file.Hunks[0].Lines[0].NewNumber == 1, "new numbering starts at 1");
        check(document.Additions == 2 && document.Deletions == 0, "both lines are additions");
    }

    private static void DeletedFile(Action<bool, string> check)
    {
        var document = DiffDocument.Parse("""
diff --git a/old.ts b/old.ts
deleted file mode 100644
index 1234567..0000000
--- a/old.ts
+++ /dev/null
@@ -1,2 +0,0 @@
-gone
-also gone
""");
        var file = document.Files[0];
        check(file.Kind == DiffFileKind.Deleted, "/dev/null on the new side means the file was deleted");
        check(file.NewPath is null && file.OldPath == "old.ts", "the surviving path is the old one");
        check(file.Hunks[0].NewCount == 0, "a deleted file has an empty new range");
        check(file.Hunks[0].Lines.All(line => line.NewNumber is null), "no line of a deleted file has a new number");
        check(document.Deletions == 2 && document.Additions == 0, "both lines are deletions");
    }

    private static void RenamedFile(Action<bool, string> check)
    {
        var document = DiffDocument.Parse("""
diff --git a/old.ts b/new.ts
similarity index 100%
rename from old.ts
rename to new.ts
""");
        var file = document.Files[0];
        check(file.Kind == DiffFileKind.Renamed, "rename lines mark the file as renamed");
        check(file.OldPath == "old.ts" && file.NewPath == "new.ts", "both rename sides are kept");
        check(file.Hunks.Count == 0, "a pure rename has no hunks");
        check(file.HeaderLineCount == file.RawLines.Count, "every line of a rename is header");
    }

    private static void BinaryFile(Action<bool, string> check)
    {
        var document = DiffDocument.Parse("""
diff --git a/logo.png b/logo.png
index 1111111..2222222 100644
Binary files a/logo.png and b/logo.png differ
""");
        var file = document.Files[0];
        check(file.IsBinary && file.Kind == DiffFileKind.Binary, "a binary notice marks the file binary");
        check(document.IsBinary, "a document whose only file is binary is binary");
        check(file.Hunks.Count == 0 && document.Additions == 0, "a binary file has no text hunks");
    }

    private static void TruncatedHunk(Action<bool, string> check)
    {
        var lines = ModifiedPatch.Split('\n');
        var document = DiffDocument.Parse(string.Join('\n', lines[..^3]));
        check(document.IsIncomplete, "a hunk cut short by the size cap is reported as incomplete");
        check(!document.Files[0].Hunks[0].IsComplete, "the hunk itself knows it was cut short");
        check(document.Additions == 5, "the additions that did arrive are still counted");
    }

    private static void HeaderWithoutCounts(Action<bool, string> check)
    {
        var document = DiffDocument.Parse("""
--- a/x.ts
+++ b/x.ts
@@ -1 +1 @@
-a
+b
""");
        var hunk = document.Files[0].Hunks[0];
        check(hunk.OldStart == 1 && hunk.OldCount == 1, "an omitted old count defaults to one line");
        check(hunk.NewStart == 1 && hunk.NewCount == 1, "an omitted new count defaults to one line");
        check(hunk.IsComplete, "the single line satisfies the header");
    }

    private static void CrlfPatch(Action<bool, string> check)
    {
        var document = DiffDocument.Parse(ModifiedPatch.Replace("\n", "\r\n", StringComparison.Ordinal));
        var file = document.Files[0];
        check(file.Lines.Count == 16, "CRLF line endings produce the same lines");
        check(file.Hunks[0].NewCount == 11, "CRLF does not corrupt the hunk header");
        check(file.Lines.All(line => !line.Text.EndsWith('\r')), "no carriage return leaks into the text");
        check(file.Lines[0].Raw == "diff --git a/src/map.ts b/src/map.ts", "raw lines drop the carriage return");
    }

    private static void MultipleFiles(Action<bool, string> check)
    {
        var document = DiffDocument.Parse(ModifiedPatch + "\n" + AddedPatch);
        check(document.Files.Count == 2, "each diff --git line starts a file");
        check(document.Files[1].Kind == DiffFileKind.Added, "the second file keeps its own kind");
        check(document.Additions == 10, "totals add up across files");
        check(document.Files[1].RawLines[0].StartsWith("diff --git", StringComparison.Ordinal), "the second file starts at its own header");
    }

    private static void EmptyPatch(Action<bool, string> check)
    {
        check(DiffDocument.Parse(null).IsEmpty, "a missing patch is empty");
        check(DiffDocument.Parse(string.Empty).IsEmpty, "an empty patch is empty");
        check(DiffDocument.Parse("   \n\n").IsEmpty, "a whitespace patch is empty");
        check(DiffDocument.Parse("").Files.Count == 0, "an empty patch has no files");
    }

    private static void QuotedPath(Action<bool, string> check)
    {
        var document = DiffDocument.Parse("""
diff --git "a/\344\270\255.ts" "b/\344\270\255.ts"
index 1111111..2222222 100644
--- "a/\344\270\255.ts"
+++ "b/\344\270\255.ts"
@@ -1 +1 @@
-a
+b
""");
        check(document.Files[0].DisplayPath == "中.ts", "octal escapes are decoded as UTF-8, not as single chars");
    }

    private static void HeaderLikeContent(Action<bool, string> check)
    {
        var document = DiffDocument.Parse("""
--- a/x.txt
+++ b/x.txt
@@ -1 +1 @@
--- a/old comment
+++ b/new comment
""");
        var hunk = document.Files[0].Hunks[0];
        check(hunk.Lines.Count == 2, "lines that look like file headers stay inside the hunk");
        check(hunk.Lines[0].Kind == DiffLineKind.Removed && hunk.Lines[0].Text == "-- a/old comment", "a removed --- line is content");
        check(hunk.Lines[1].Kind == DiffLineKind.Added && hunk.Lines[1].Text == "++ b/new comment", "an added +++ line is content");
        check(hunk.IsComplete && document.Additions == 1 && document.Deletions == 1, "the hunk still satisfies its counts");
    }
}
