using RelaxKonOS.UI.FileBrowser;
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
                : StringComparer.CurrentCultureIgnoreCase.Compare(FileName.Extension(x.Name), FileName.Extension(y.Name)),
            _ => FileName.CompareNaturally(x.Name, y.Name),
        };
        if (value == 0) value = FileName.CompareNaturally(x.Name, y.Name);
        if (value == 0) value = StringComparer.Ordinal.Compare(x.Path, y.Path);
        return descending ? -Math.Sign(value) : Math.Sign(value);
    }
}

public sealed record ExplorerViewPreferences(
    ExplorerSortField SortField = ExplorerSortField.Name,
    bool SortDescending = false,
    bool ShowHiddenFiles = false,
    bool IsCompactView = false,
    ExplorerViewMode ViewMode = ExplorerViewMode.Details,
    bool FileListCacheEnabled = false);
