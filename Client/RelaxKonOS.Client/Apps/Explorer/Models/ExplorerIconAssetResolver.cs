using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Models;

/// <summary>Resolves Explorer entries to the generated PNG asset that best describes them.</summary>
public static class ExplorerIconAssetResolver
{
    public static string ForEntry(FileSystemEntryType type, string? name)
    {
        if (type == FileSystemEntryType.Drive) return "navigation-drive";
        if (type == FileSystemEntryType.Directory) return "file-folder";

        var normalizedName = name?.Trim().ToLowerInvariant() ?? string.Empty;
        var specialName = normalizedName switch
        {
            "dockerfile" => "file-dockerfile",
            "makefile" => "file-makefile",
            "cmakelists.txt" => "file-cmake",
            "package.json" or "package-lock.json" or "composer.json" or "gemfile" => "file-package",
            ".gitignore" or ".gitattributes" or ".gitmodules" => "file-git-config",
            ".env" or ".env.local" or ".env.production" => "file-env",
            _ => null
        };
        if (specialName is not null) return specialName;

        return ExplorerPath.Extension(normalizedName).ToLowerInvariant() switch
        {
            ".c" or ".h" => "file-c",
            ".cpp" or ".cxx" or ".cc" or ".hpp" or ".hxx" => "file-cpp",
            ".cs" or ".csx" => "file-csharp",
            ".fs" or ".fsx" or ".fsi" => "file-fsharp",
            ".vb" => "file-visual-basic",
            ".java" => "file-java",
            ".kt" or ".kts" => "file-kotlin",
            ".go" => "file-go",
            ".rs" => "file-rust",
            ".py" or ".pyw" => "file-python",
            ".js" or ".mjs" or ".cjs" => "file-javascript",
            ".ts" => "file-typescript",
            ".tsx" or ".jsx" => "file-react",
            ".html" or ".htm" => "file-html",
            ".css" => "file-css",
            ".scss" or ".sass" => "file-sass",
            ".less" => "file-css-alt",
            ".php" => "file-php",
            ".rb" => "file-ruby",
            ".swift" => "file-swift",
            ".dart" => "file-dart",
            ".sh" or ".bash" or ".zsh" or ".fish" => "file-shell",
            ".ps1" or ".psm1" => "file-powershell",
            ".sql" => "file-sql",
            ".json" => "file-json",
            ".xml" or ".xaml" => "file-xml",
            ".yml" or ".yaml" => "file-yaml",
            ".toml" => "file-toml",
            ".ini" or ".cfg" or ".conf" or ".properties" => "file-config",
            ".csproj" or ".fsproj" or ".vbproj" or ".sln" or ".slnx" => "file-dotnet-project",
            _ => ForGeneralEntry(type, normalizedName)
        };
    }

    public static string ForTreeNode(TreeNodeIconKind kind) => kind switch
    {
        TreeNodeIconKind.Computer => "navigation-computer",
        TreeNodeIconKind.Drive => "navigation-drive",
        TreeNodeIconKind.Folder => "file-folder",
        TreeNodeIconKind.Home => "navigation-home",
        TreeNodeIconKind.Desktop => "navigation-desktop",
        TreeNodeIconKind.Documents => "navigation-documents",
        TreeNodeIconKind.Downloads => "file-folder",
        TreeNodeIconKind.Pictures => "navigation-pictures",
        TreeNodeIconKind.Music => "navigation-music",
        TreeNodeIconKind.Videos => "navigation-videos",
        TreeNodeIconKind.Network => "navigation-network",
        _ => "file-folder"
    };

    private static string ForGeneralEntry(FileSystemEntryType type, string normalizedName)
    {
        var kind = ExplorerFileIconKindResolver.ForEntry(type, normalizedName);
        return kind switch
        {
            ExplorerFileIconKind.Folder => "file-folder",
            ExplorerFileIconKind.Document => "file-document",
            ExplorerFileIconKind.Spreadsheet => "file-spreadsheet",
            ExplorerFileIconKind.Presentation => "file-presentation",
            ExplorerFileIconKind.Pdf => "file-pdf",
            ExplorerFileIconKind.Image => "file-image",
            ExplorerFileIconKind.Audio => "file-audio",
            ExplorerFileIconKind.Video => "file-video",
            ExplorerFileIconKind.Archive => "file-archive",
            ExplorerFileIconKind.Data => "file-config",
            ExplorerFileIconKind.Database => "file-database",
            ExplorerFileIconKind.Application => "file-application",
            ExplorerFileIconKind.DiskImage => "file-disk-image",
            ExplorerFileIconKind.Font => "file-font",
            ExplorerFileIconKind.Code => "file-code-generic",
            _ => "file-generic"
        };
    }
}
