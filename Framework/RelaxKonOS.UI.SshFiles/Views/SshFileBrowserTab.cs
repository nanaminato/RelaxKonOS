using RelaxKonOS.AppSDK;
using RelaxKonOS.UI.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RelaxKonOS.UI.SshFiles;

/// <summary>Window-local SFTP directory snapshot and independent navigation state.</summary>
internal sealed partial class SshFileBrowserTab(ISystemLanguage language) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(IconKind))]
    private string _path = ".";
    public void RefreshLocalizedText() => OnPropertyChanged(nameof(Title));
    public string Title => Path == "." ? SshFilesLocalization.Get(language, "ssh_files.home")
        : Path == "/" ? "/" : Path.TrimEnd('/').Split('/').Last();
    public TreeNodeIconKind IconKind => Path == "/" ? TreeNodeIconKind.Drive : TreeNodeIconKind.Folder;
    public List<string> History { get; } = [];
    public int HistoryIndex { get; set; } = -1;
    public SshFileEntry[] Entries { get; set; } = [];
    public string SearchText { get; set; } = string.Empty;
    public int SortIndex { get; set; }
    public bool Descending { get; set; }
    public bool ShowHidden { get; set; }
    public int ViewModeIndex { get; set; }
}
