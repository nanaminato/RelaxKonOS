using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.ViewModels;

public sealed partial class ExplorerViewModel
{
    private const int HostSaveMaximumDepth = 128;
    private const int HostSaveMaximumEntries = 100_000;

    public bool CanSaveToHost => HasSelection && !IsPickerMode;

    [RelayCommand]
    private async Task SaveToHostAsync()
    {
        if (!CanSaveToHost) return;
        var entries = NormalizeBatchSelection(GetSelectedEntries());
        if (entries.Count == 0) return;

        var destination = await PickHostSaveDirectoryAsync();
        if (destination is null) return;

        try
        {
            var plan = new List<HostSaveItem>();
            foreach (var entry in entries)
                await BuildHostSavePlanAsync(entry, destination, plan, 0);

            var files = plan.Where(item => !item.IsDirectory).ToArray();
            var operationItems = files.Select(item => new FileOperationItem(item.RemotePath, item.LocalPath)).ToArray();
            var totalBytes = files.Sum(item => item.Size);

            async Task DownloadAsync(Action<string, long, int> report, CancellationToken cancellationToken)
            {
                foreach (var directory in plan.Where(item => item.IsDirectory))
                    Directory.CreateDirectory(directory.LocalPath);

                var completed = 0;
                foreach (var item in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!);
                    var download = await _client.DownloadAsync(item.RemotePath, cancellationToken)
                        ?? throw new FileNotFoundException(LocalizedText.Get("explorer.status.download_file_missing"), item.RemotePath);
                    await using var source = download.Value.Stream;
                    await using var target = new FileStream(item.LocalPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);
                    var buffer = new byte[1024 * 128];
                    long written = 0;
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, cancellationToken);
                        if (read == 0) break;
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        written += read;
                        report(item.RemotePath, written, completed);
                    }
                    completed++;
                    report(item.RemotePath, written, completed);
                }
            }

            if (QueueDownload is not null)
                QueueDownload(operationItems, totalBytes, DownloadAsync);
            else
            {
                await DownloadAsync((_, _, _) => { }, CancellationToken.None);
                StatusText = LocalizedText.Ref("explorer.status.downloaded", destination);
            }
        }
        catch (Exception ex)
        {
            StatusText = LocalizedText.Ref("explorer.status.download_failed", ex.Message);
        }
    }

    private async Task BuildHostSavePlanAsync(FileSystemEntryDto entry, string localParent,
        List<HostSaveItem> plan, int depth)
    {
        if (depth > HostSaveMaximumDepth)
            throw new IOException($"Directory nesting exceeds {HostSaveMaximumDepth} levels.");
        if (plan.Count >= HostSaveMaximumEntries)
            throw new IOException($"Selection contains more than {HostSaveMaximumEntries:N0} entries.");

        var localPath = Path.Combine(localParent, entry.Name);
        if (entry.Type == FileSystemEntryType.File)
        {
            plan.Add(new(entry.Path, localPath, Math.Max(0, entry.Size ?? 0), false));
            return;
        }
        if (entry.Type == FileSystemEntryType.Drive)
            throw new IOException("Saving an entire drive to the host is not supported.");

        plan.Add(new(entry.Path, localPath, 0, true));
        var directory = await _client.GetDirectoryAsync(entry.Path);
        foreach (var child in directory.Directories)
            await BuildHostSavePlanAsync(child, localPath, plan, depth + 1);
        foreach (var file in directory.Files)
        {
            if (plan.Count >= HostSaveMaximumEntries)
                throw new IOException($"Selection contains more than {HostSaveMaximumEntries:N0} entries.");
            plan.Add(new(file.Path, Path.Combine(localPath, file.Name), Math.Max(0, file.Size), false));
        }
    }

    private static async Task<string?> PickHostSaveDirectoryAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            return null;
        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LocalizedText.Get("explorer.operations.download"),
            AllowMultiple = false,
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private sealed record HostSaveItem(string RemotePath, string LocalPath, long Size, bool IsDirectory);
}
