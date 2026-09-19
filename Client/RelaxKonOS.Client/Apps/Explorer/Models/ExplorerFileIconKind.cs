using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Models;

/// <summary>Visual categories used by Explorer's extension-aware vector icons.</summary>
public enum ExplorerFileIconKind
{
    Drive,
    Folder,
    File,
    Document,
    Spreadsheet,
    Presentation,
    Pdf,
    Image,
    Audio,
    Video,
    Archive,
    Code,
    Data,
    Database,
    Application,
    DiskImage,
    Font,
    Home,
    Desktop,
    Downloads,
    Network
}

/// <summary>Classifies file names into stable, platform-independent Explorer icon categories.</summary>
public static class ExplorerFileIconKindResolver
{
    public static ExplorerFileIconKind ForEntry(FileSystemEntryType type, string? name)
    {
        if (type == FileSystemEntryType.Drive) return ExplorerFileIconKind.Drive;
        if (type == FileSystemEntryType.Directory) return ExplorerFileIconKind.Folder;

        var extension = ExplorerPath.Extension(name ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            // Office and reading
            ".doc" or ".docx" or ".odt" or ".rtf" or ".pages" or ".epub" or ".mobi" => ExplorerFileIconKind.Document,
            ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".ods" or ".csv" or ".tsv" => ExplorerFileIconKind.Spreadsheet,
            ".ppt" or ".pptx" or ".pps" or ".ppsx" or ".odp" or ".key" => ExplorerFileIconKind.Presentation,
            ".pdf" => ExplorerFileIconKind.Pdf,

            // Media
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico" or ".tif" or ".tiff" or ".heic" or ".avif" or ".raw" or ".psd" => ExplorerFileIconKind.Image,
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac" or ".wma" or ".opus" or ".aiff" => ExplorerFileIconKind.Audio,
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".flv" or ".m4v" or ".mpeg" or ".mpg" => ExplorerFileIconKind.Video,

            // Packages and installable artifacts
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2" or ".xz" or ".zst" or ".cab" or ".jar" or ".war" or ".whl" => ExplorerFileIconKind.Archive,
            ".exe" or ".msi" or ".msix" or ".appx" or ".appimage" or ".deb" or ".rpm" or ".apk" or ".dmg" or ".pkg" => ExplorerFileIconKind.Application,
            ".iso" or ".img" or ".vhd" or ".vhdx" or ".vmdk" or ".qcow" or ".qcow2" => ExplorerFileIconKind.DiskImage,

            // Development and structured data
            ".cs" or ".csx" or ".fs" or ".vb" or ".c" or ".h" or ".cpp" or ".cxx" or ".hpp" or ".java" or ".kt" or ".kts" or ".go" or ".rs" or ".py" or ".rb" or ".php" or ".swift" or ".scala" or ".sh" or ".bash" or ".ps1" or ".bat" or ".cmd" or ".js" or ".mjs" or ".cjs" or ".ts" or ".tsx" or ".jsx" or ".vue" or ".svelte" or ".html" or ".htm" or ".css" or ".scss" or ".sass" or ".less" or ".sql" => ExplorerFileIconKind.Code,
            ".json" or ".xml" or ".yml" or ".yaml" or ".toml" or ".ini" or ".cfg" or ".conf" or ".properties" or ".env" or ".log" => ExplorerFileIconKind.Data,
            ".db" or ".sqlite" or ".sqlite3" or ".mdb" or ".accdb" or ".dbf" => ExplorerFileIconKind.Database,
            ".ttf" or ".otf" or ".woff" or ".woff2" or ".eot" => ExplorerFileIconKind.Font,
            ".txt" or ".md" or ".markdown" or ".rst" or ".tex" => ExplorerFileIconKind.Document,
            _ when IsSourceName(name) => ExplorerFileIconKind.Code,
            _ => ExplorerFileIconKind.File
        };
    }

    public static ExplorerFileIconKind ForTreeNode(TreeNodeIconKind kind) => kind switch
    {
        TreeNodeIconKind.Computer or TreeNodeIconKind.Drive => ExplorerFileIconKind.Drive,
        TreeNodeIconKind.Folder => ExplorerFileIconKind.Folder,
        TreeNodeIconKind.Home => ExplorerFileIconKind.Home,
        TreeNodeIconKind.Desktop => ExplorerFileIconKind.Desktop,
        TreeNodeIconKind.Documents => ExplorerFileIconKind.Document,
        TreeNodeIconKind.Downloads => ExplorerFileIconKind.Downloads,
        TreeNodeIconKind.Pictures => ExplorerFileIconKind.Image,
        TreeNodeIconKind.Music => ExplorerFileIconKind.Audio,
        TreeNodeIconKind.Videos => ExplorerFileIconKind.Video,
        TreeNodeIconKind.Network => ExplorerFileIconKind.Network,
        _ => ExplorerFileIconKind.Folder
    };

    private static bool IsSourceName(string? name) => name is not null && name.ToLowerInvariant() is
        "dockerfile" or "makefile" or "cmakelists.txt" or "gemfile" or "rakefile" or "procfile";
}
