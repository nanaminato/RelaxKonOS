using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Models;

public enum ExplorerSortField { Name, Modified, Type, Size }

/// <summary>Folder-first ordering with numeric filename runs (file2 before file10).
/// Digit runs are compared by length, never parsed into a bounded integer.</summary>
public sealed class ExplorerEntryComparer(ExplorerSortField field, bool descending) : IComparer<FileSystemEntryDto>
{
    public int Compare(FileSystemEntryDto? x, FileSystemEntryDto? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var group = (x.Type == FileSystemEntryType.File ? 1 : 0).CompareTo(y.Type == FileSystemEntryType.File ? 1 : 0);
        if (group != 0) return group; // Descending reverses values, not the folder/file groups.
        var value = field switch
        {
            ExplorerSortField.Modified => Nullable.Compare(x.Modified, y.Modified),
            ExplorerSortField.Size => Nullable.Compare(x.Size, y.Size),
            ExplorerSortField.Type => x.Type != FileSystemEntryType.File ? 0
                : StringComparer.CurrentCultureIgnoreCase.Compare(ExplorerPath.Extension(x.Name), ExplorerPath.Extension(y.Name)),
            _ => CompareNames(x.Name, y.Name),
        };
        if (value == 0) value = CompareNames(x.Name, y.Name);
        if (value == 0) value = StringComparer.Ordinal.Compare(x.Path, y.Path);
        return descending ? -Math.Sign(value) : Math.Sign(value);
    }

    public static int CompareNames(string x, string y)
    {
        var i = 0;
        var j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var a = i;
                var b = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                while (a < i && x[a] == '0') a++;
                while (b < j && y[b] == '0') b++;
                var length = (i - a).CompareTo(j - b);
                if (length != 0) return length;
                var number = x.AsSpan(a, i - a).SequenceCompareTo(y.AsSpan(b, j - b));
                if (number != 0) return number;
            }
            else
            {
                var value = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (value != 0) return value;
                i++;
                j++;
            }
        }
        var remaining = (x.Length - i).CompareTo(y.Length - j);
        return remaining != 0 ? remaining : StringComparer.Ordinal.Compare(x, y);
    }
}

public sealed record ExplorerViewPreferences(
    ExplorerSortField SortField = ExplorerSortField.Name,
    bool SortDescending = false,
    bool ShowHiddenFiles = false,
    bool IsCompactView = false);
