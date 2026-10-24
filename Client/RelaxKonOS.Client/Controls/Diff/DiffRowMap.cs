using System.Globalization;
using System.Text;

namespace RelaxKonOS.Client.Controls.Diff;

/// <summary>One rendered patch line: what it is and which numbers its two gutter columns show.</summary>
/// <param name="Kind">The role the line plays in the patch.</param>
/// <param name="OldNumber">The number on the old side, when the line exists there.</param>
/// <param name="NewNumber">The number on the new side, when the line exists there.</param>
public readonly record struct DiffRow(DiffLineKind Kind, int? OldNumber, int? NewNumber);

/// <summary>
/// The exact text a diff view hands to its editor, plus the row metadata the renderers index by
/// document line number.
/// </summary>
/// <remarks>
/// The invariant every renderer depends on: <see cref="Rows"/> has one entry per document line, in
/// the same order, and no row text contains a line break. <see cref="DiffFile.RawLines"/> satisfies
/// that because the parser splits on newlines, so the projection is a join and nothing else — no
/// markers are stripped and no lines are added between files.
/// </remarks>
internal sealed class DiffRowMap
{
    public static DiffRowMap Empty { get; } = new(string.Empty, [], 1, 1);

    private DiffRowMap(string text, IReadOnlyList<DiffRow> rows, int oldDigits, int newDigits)
    {
        Text = text;
        Rows = rows;
        OldDigits = oldDigits;
        NewDigits = newDigits;
    }

    /// <summary>The patch text as the editor receives it.</summary>
    public string Text { get; }

    public IReadOnlyList<DiffRow> Rows { get; }

    /// <summary>Gutter widths in digits, so the columns do not resize while scrolling.</summary>
    public int OldDigits { get; }

    public int NewDigits { get; }

    /// <summary>The row for a 1-based document line number; out-of-range lines read as metadata.</summary>
    public DiffRow At(int documentLineNumber)
        => documentLineNumber >= 1 && documentLineNumber <= Rows.Count
            ? Rows[documentLineNumber - 1]
            : new DiffRow(DiffLineKind.Context, null, null);

    public static DiffRowMap Create(DiffDocument? document)
    {
        if (document is null)
            return Empty;

        var text = new StringBuilder();
        var rows = new List<DiffRow>();
        var oldDigits = 1;
        var newDigits = 1;

        foreach (var file in document.Files)
        {
            foreach (var line in file.Lines)
            {
                if (rows.Count > 0)
                    text.Append('\n');
                text.Append(line.Raw);
                rows.Add(new DiffRow(line.Kind, line.OldNumber, line.NewNumber));

                if (line.OldNumber is { } old)
                    oldDigits = Math.Max(oldDigits, Digits(old));
                if (line.NewNumber is { } current)
                    newDigits = Math.Max(newDigits, Digits(current));
            }
        }

        return rows.Count == 0 ? Empty : new DiffRowMap(text.ToString(), rows, oldDigits, newDigits);
    }

    private static int Digits(int value) => value.ToString(CultureInfo.InvariantCulture).Length;
}
