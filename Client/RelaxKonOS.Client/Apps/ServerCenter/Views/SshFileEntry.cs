using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.ServerCenter.Views;

internal sealed record SshFileEntry(string Name, string Path, bool IsDirectory, bool IsLink, long Size, DateTime Modified)
{
    /// <summary>
    /// Icon input for the shared Explorer artwork. <see cref="IsLink"/> is passed separately, so
    /// the resolver only has to classify directories and files here.
    /// </summary>
    public FileSystemEntryType EntryType =>
        IsDirectory ? FileSystemEntryType.Directory : FileSystemEntryType.File;

    public string Kind => IsLink ? LocalizedText.Get("ssh_files.link", "Link")
        : IsDirectory ? LocalizedText.Get("ssh_files.folder", "Folder") : LocalizedText.Get("ssh_files.file", "File");
    public string SizeText => IsDirectory ? "" : $"{Size:N0} B";
    public string ModifiedText => Modified.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
}
