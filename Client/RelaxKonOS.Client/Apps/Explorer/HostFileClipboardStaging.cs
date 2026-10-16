using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer;

/// <summary>Private disk snapshots shared by file-URL clipboard backends.</summary>
internal static class HostFileClipboardStaging
{
    private static readonly string CacheRoot = Path.Combine(Path.GetTempPath(), "relaxkonos-clipboard-" + Guid.NewGuid().ToString("N"));
    internal static readonly SemaphoreSlim Publication = new(1);

    static HostFileClipboardStaging()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Delete(CacheRoot);
    }

    internal static string CreateRoot()
    {
        CreatePrivateDirectory(CacheRoot);
        return Path.Combine(CacheRoot, Guid.NewGuid().ToString("N"));
    }

    internal static async Task<IReadOnlyList<string>> PrepareAsync(string root, IExplorerClient client,
        IReadOnlyList<FileSystemEntryDto> entries, Func<bool> valid,
        Func<string, long, ClipboardFileTransfer> beginTransfer, Action<string> validateComponent,
        StringComparer comparer, Func<string, string> normalize)
    {
        var names = new HashSet<string>(comparer);
        var paths = new List<string>();
        var count = 0;
        void Check() { if (!valid()) throw new IOException("The source session has changed."); }
        async Task Add(string remote, string relative, bool directory, long size, int depth)
        {
            Check();
            if (++count > 100000 || depth > 128) throw new IOException("The clipboard selection is too large.");
            if (!names.Add(normalize(relative)))
                throw new IOException("The selection contains duplicate host file names.");
            var local = Path.Combine(root, relative);
            if (directory)
            {
                Directory.CreateDirectory(local);
                var listing = await client.GetDirectoryAsync(remote).ConfigureAwait(false);
                Check();
                foreach (var child in listing.Directories)
                {
                    validateComponent(child.Name);
                    await Add(child.Path, Path.Combine(relative, child.Name), true, 0, depth + 1);
                }
                foreach (var child in listing.Files)
                {
                    validateComponent(child.Name);
                    await Add(child.Path, Path.Combine(relative, child.Name), false, child.Size, depth + 1);
                }
                return;
            }
            if (size < 0) throw new IOException("Invalid source file size.");
            var transfer = beginTransfer(remote, size);
            try
            {
                transfer.CancellationToken.ThrowIfCancellationRequested();
                var download = await client.DownloadAsync(remote, transfer.CancellationToken).ConfigureAwait(false)
                    ?? throw new FileNotFoundException(remote);
                {
                    await using var source = download.Stream;
                    Check();
                    await using var output = new FileStream(local, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                    var buffer = new byte[131072];
                    long received = 0;
                    while (true)
                    {
                        Check();
                        var read = await source.ReadAsync(buffer, transfer.CancellationToken).ConfigureAwait(false);
                        Check();
                        if (read == 0) break;
                        received = checked(received + read);
                        if (received > size) throw new IOException("The source file changed during download.");
                        await output.WriteAsync(buffer.AsMemory(0, read), transfer.CancellationToken).ConfigureAwait(false);
                        transfer.Report(received);
                    }
                    if (received != size) throw new EndOfStreamException("The source file changed during download.");
                    transfer.CancellationToken.ThrowIfCancellationRequested();
                    await output.FlushAsync(transfer.CancellationToken).ConfigureAwait(false);
                }
                Check();
                transfer.Complete(null);
            }
            catch (Exception ex) { transfer.Complete(ex); throw; }
        }
        try
        {
            Check();
            if (entries.Count == 0) throw new IOException("The clipboard selection is empty.");
            CreatePrivateDirectory(root);
            foreach (var entry in entries)
            {
                validateComponent(entry.Name);
                await Add(entry.Path, entry.Name, entry.Type != FileSystemEntryType.File, entry.Size ?? 0, 0);
                paths.Add(Path.Combine(root, entry.Name));
            }
            Check();
            return paths;
        }
        catch { Delete(root); throw; }
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

}
