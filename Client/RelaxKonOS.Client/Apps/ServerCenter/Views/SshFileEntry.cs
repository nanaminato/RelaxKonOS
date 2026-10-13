using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.ServerCenter.Views;

internal sealed class SshFileEntry(string name, string path, bool isDirectory, bool isLink, long size, DateTime modified) : ObservableObject
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public bool IsDirectory { get; } = isDirectory;
    public bool IsLink { get; } = isLink;
    public long Size { get; } = size;
    public DateTime Modified { get; } = modified;
    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }
    private string _renameDraft = name;
    public string RenameDraft { get => _renameDraft; set => SetProperty(ref _renameDraft, value); }
    private double _cutOpacity = 1;
    public double CutOpacity { get => _cutOpacity; set => SetProperty(ref _cutOpacity, value); }

    /// <summary>
    /// Icon input for the shared Explorer artwork. <see cref="IsLink"/> is passed separately, so
    /// the resolver only has to classify directories and files here.
    /// </summary>
    public FileSystemEntryType EntryType =>
        IsDirectory ? FileSystemEntryType.Directory : FileSystemEntryType.File;

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(ModifiedText));
    }

    public string Kind => IsLink ? LocalizedText.Get("ssh_files.link", "Link")
        : IsDirectory ? LocalizedText.Get("ssh_files.folder", "Folder") : LocalizedText.Get("ssh_files.file", "File");
    public string SizeText => IsDirectory ? "" : $"{Size:N0} B";
    public string ModifiedText => Modified.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
}
