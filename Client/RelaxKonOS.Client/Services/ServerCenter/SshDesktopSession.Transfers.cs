using Renci.SshNet;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.ServerCenter;

public sealed partial class SshDesktopSession
{
    internal Task<bool> UploadDirectoryAsync(ServerCenterSshEndpoint endpoint, DirectoryInfo directory, string target,
        Action<string, long, long?> report, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var client = OpenFileClient(endpoint);
        if (client.Exists(target)) return false;
        UploadTree(client, directory, target, report, cancellationToken);
        return true;
    }, cancellationToken);

    internal Task DownloadPathsAsync(ServerCenterSshEndpoint endpoint, string directory,
        IReadOnlyList<(string Path, string Name, bool Directory)> paths, Action<string, long, long?> report,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var client = OpenFileClient(endpoint);
        foreach (var (path, name, isDirectory) in paths)
            DownloadTree(client, path, SafeLocalChild(directory, name), isDirectory, report, cancellationToken);
    }, cancellationToken);

    private static string RemoteChild(string directory, string name) => directory.TrimEnd('/') + "/" + name;

    private static void UploadTree(SftpClient client, DirectoryInfo directory, string target, Action<string, long, long?> report, CancellationToken cancellationToken)
    {
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"{directory.FullName}: {LocalizedText.Get("ssh_files.link_unsupported", "Symbolic link transfer is unsupported")}");
        cancellationToken.ThrowIfCancellationRequested();
        client.CreateDirectory(target);
        foreach (var file in directory.EnumerateFiles())
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"{file.FullName}: {LocalizedText.Get("ssh_files.link_unsupported", "Symbolic link transfer is unsupported")}");
            using var input = file.OpenRead();
            report(file.FullName, 0, input.Length);
            client.UploadFile(input, RemoteChild(target, file.Name), canOverride: false,
                uploaded => report(file.FullName, (long)uploaded, input.Length));
        }
        foreach (var child in directory.EnumerateDirectories())
            UploadTree(client, child, RemoteChild(target, child.Name), report, cancellationToken);
    }

    private static void DownloadTree(SftpClient client, string source, string destination, bool directory, Action<string, long, long?> report, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!directory)
        {
            var size = client.GetAttributes(source).Size;
            report(source, 0, size);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            client.DownloadFile(source, output, downloaded => report(source, (long)downloaded, size));
            return;
        }
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException($"{LocalizedText.Get("ssh_files.exists", "Destination already exists")}: {destination}");
        Directory.CreateDirectory(destination);
        foreach (var child in client.ListDirectory(source).Where(file => file.Name is not ("." or "..")))
        {
            if (child.IsSymbolicLink) throw new IOException($"{child.FullName}: {LocalizedText.Get("ssh_files.link_unsupported", "Symbolic link transfer is unsupported")}");
            DownloadTree(client, child.FullName, SafeLocalChild(destination, child.Name), child.IsDirectory, report, cancellationToken);
        }
    }

    private static string SafeLocalChild(string directory, string name)
    {
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/') || name.Contains('\\') || Path.GetFileName(name) != name)
            throw new IOException($"{LocalizedText.Get("ssh_files.invalid_local_name", "File name is not valid on this device")}: {name}");
        return Path.Combine(directory, name);
    }
}
