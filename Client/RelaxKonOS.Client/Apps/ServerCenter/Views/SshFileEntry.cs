using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.ServerCenter.Views;

internal sealed record SshFileEntry(string Name, string Path, bool IsDirectory, bool IsLink, long Size, DateTime Modified)
{
    public string Icon => IsLink ? "🔗" : IsDirectory ? "📁" : "📄";
    public string Kind => IsLink ? LocalizedText.Get("ssh_files.link", "Link")
        : IsDirectory ? LocalizedText.Get("ssh_files.folder", "Folder") : LocalizedText.Get("ssh_files.file", "File");
    public string SizeText => IsDirectory ? "" : $"{Size:N0} B";
    public string ModifiedText => Modified.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
}
