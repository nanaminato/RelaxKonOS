using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.ViewModels.Shell;

/// <summary>One file-system item shown on the user's remote desktop.</summary>
public sealed partial class DesktopFileEntryViewModel : ObservableObject
{
    public DesktopFileEntryViewModel(FileSystemEntryDto entry)
    {
        Entry = entry;
        IconGlyph = GetIcon(entry);
    }

    public FileSystemEntryDto Entry { get; }
    public string DisplayName => Entry.Name;
    public string IconGlyph { get; }
    public bool IsDirectory => Entry.Type is FileSystemEntryType.Directory or FileSystemEntryType.Drive;
    [ObservableProperty] private bool _isDesktopSelected;

    private static string GetIcon(FileSystemEntryDto entry)
    {
        if (entry.Type is FileSystemEntryType.Directory or FileSystemEntryType.Drive)
            return "📁";

        return Path.GetExtension(entry.Name).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" => "🖼️",
            ".txt" or ".md" or ".log" or ".json" or ".xml" or ".yml" or ".yaml" => "📄",
            ".cs" or ".js" or ".ts" or ".py" or ".java" or ".cpp" or ".c" or ".html" or ".css" => "💻",
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "🗜️",
            ".pdf" => "📕",
            ".mp3" or ".wav" or ".flac" or ".ogg" => "🎵",
            ".mp4" or ".mkv" or ".avi" or ".mov" => "🎬",
            _ => "📄",
        };
    }
}
