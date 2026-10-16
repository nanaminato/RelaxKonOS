using System.Diagnostics;
using System.IO.Compression;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Server.Files;

public static class ArchiveProviderChecks
{
    public static async Task RunAsync(string root)
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        async Task Reject(Func<Task> action, string message)
        {
            try { await action(); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or FileNotFoundException or OperationCanceledException or ArchivePasswordException)
            { checks++; return; }
            throw new Exception(message);
        }
        foreach (var format in new[] { FileArchiveFormat.Zip, FileArchiveFormat.Tar, FileArchiveFormat.TarGZip })
        {
            var source = Path.Combine(root, "preview-" + format + ".txt");
            await File.WriteAllTextAsync(source, "hello 预览");
            var path = Path.Combine(root, "preview" + FileArchiveFormats.Extension(format));
            await FileArchiveService.ExecuteAsync(new LocalArchiveFileSystem(), FileOperationKind.Compress, [source], path,
                (_, _, _, _) => { }, (_, error) => throw error, default);
            await using var input = File.OpenRead(path);
            await using var archive = await FileArchiveService.OpenReaderAsync(input, path, default);
            var entry = archive.Entries.Single();
            Check(entry.Name == Path.GetFileName(source), format + " lists BCL entries");
            Check(System.Text.Encoding.UTF8.GetString(await FileArchiveService.ReadPreviewAsync(archive, entry.Name, default)) == "hello 预览", format + " previews one file");
            await Reject(async () => { await FileArchiveService.ReadPreviewAsync(archive, "../escape", default); }, "Unknown archive paths must fail");
        }
        foreach (var metadata in new[]
        {
            "Path = ../escape\nSize = 0\n",
            "Path = a\nSize = 0\n\nPath = a/b\nSize = 0\n",
            "Path = A\nSize = 0\n\nPath = a\nSize = 0\n",
            "Path = link\nSize = 0\nSymbolic Link = target\n",
            "Path = link\nSize = 0\nAttributes = A lrwxrwxrwx\n",
            "Path = file\nSize = 0\nEncrypted = +\n",
            "Path = file\nSize = 0\nSize = 1\n",
            "Path = file\nSize = 4294967297\n",
            "Path = file\nSize = 0\nAlternate Stream = +\n"
        }) await Reject(() => Task.Run(() => SevenZipArchiveProvider.ParseListing(metadata)), "Unsafe metadata accepted");

        var executable = Path.Combine(AppContext.BaseDirectory, "Assets", "SevenZip",
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, OperatingSystem.IsWindows() ? "7z.exe" : "7zz");
        Check(File.Exists(executable), "Tests require the bundled real CLI");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var sourceDirectory = Path.Combine(root, "cli-source"); Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "empty"));
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "中文.txt"), "7-Zip preview 内容");
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "-switch.txt"), "literal entry");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "@"));
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "@", "list.txt"), "literal listfile-shaped entry");
        var seven = Path.Combine(root, "actual.7z");
        var start = new ProcessStartInfo(executable) { WorkingDirectory = sourceDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        // The end-of-switch marker makes option-like member names literal.
        foreach (var arg in new[] { "a", "-t7z", "--", seven, "中文.txt", "-switch.txt", "empty", "./@" }) start.ArgumentList.Add(arg);
        using (var process = Process.Start(start)!)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Check(process.ExitCode == 0, "Real 7z fixture creation: " + await stderr); await stdout;
        }
        await using (var input = File.OpenRead(seven))
        await using (var archive = await FileArchiveService.OpenReaderAsync(input, seven, default))
        {
            Check(archive.Entries.Count == 5 && archive.Entries.Any(e => e.Name == "empty" && e.IsDirectory), "CLI lists Unicode, option-like names and empty directories");
            Check(System.Text.Encoding.UTF8.GetString(await FileArchiveService.ReadPreviewAsync(archive, "中文.txt", default)) == "7-Zip preview 内容", "CLI previews Unicode content");
            Check(System.Text.Encoding.UTF8.GetString(await FileArchiveService.ReadPreviewAsync(archive, "-switch.txt", default)) == "literal entry", "CLI treats entry names as literal arguments");
            Check(System.Text.Encoding.UTF8.GetString(await FileArchiveService.ReadPreviewAsync(archive, "@/list.txt", default)) == "literal listfile-shaped entry", "CLI never interprets archive member names as host listfiles");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Reject(async () => { await archive.CopyEntryAsync("中文.txt", Stream.Null, cancellation.Token); }, "CLI ignores cancellation");
        }
        var extracted = Path.Combine(root, "cli-extracted");
        foreach (var (suffix, arguments) in new[]
        {
            (".zip", new[] { "-tzip", "-mm=BZip2" }),
            (".xz", new[] { "-txz" }),
            (".bz2", new[] { "-tbzip2" })
        })
        {
            var variant = Path.Combine(root, "variant" + suffix);
            start.ArgumentList.Clear(); start.ArgumentList.Add("a");
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("--"); start.ArgumentList.Add(variant); start.ArgumentList.Add("中文.txt");
            using (var process = Process.Start(start)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Check(process.ExitCode == 0, "Real variant fixture: " + await stderr); await stdout;
            }
            await using var input = File.OpenRead(variant);
            await using var archive = await FileArchiveService.OpenReaderAsync(input, variant, default);
            Check(System.Text.Encoding.UTF8.GetString(await FileArchiveService.ReadPreviewAsync(archive, archive.Entries.Single().Name, default)) == "7-Zip preview 内容", suffix + " CLI selection previews content");
            var variantOutput = Path.Combine(root, "variant-output-" + suffix[1..]);
            await FileArchiveService.ExecuteAsync(new LocalArchiveFileSystem(), FileOperationKind.Extract, [variant], variantOutput,
                (_, _, _, _) => { }, (_, error) => throw error, default);
            Check(Directory.GetFiles(variantOutput).Length == 1, suffix + " uses validated extraction");
        }
        await FileArchiveService.ExecuteAsync(new LocalArchiveFileSystem(), FileOperationKind.Extract, [seven], extracted,
            (_, _, _, _) => { }, (_, error) => throw error, default);
        Check(await File.ReadAllTextAsync(Path.Combine(extracted, "中文.txt")) == "7-Zip preview 内容" && Directory.Exists(Path.Combine(extracted, "empty")), "CLI extraction publishes validated content");
        var failed = Path.Combine(root, "cli-cancelled");
        using (var cancellation = new CancellationTokenSource())
            await Reject(() => FileArchiveService.ExecuteAsync(new LocalArchiveFileSystem(), FileOperationKind.Extract, [seven], failed,
                (_, _, _, _) => cancellation.Cancel(), (_, error) => throw error, cancellation.Token), "Cancelled CLI extraction published output");
        Check(!Directory.Exists(failed) && !Directory.EnumerateDirectories(root, ".relaxkonos-archive-*").Any(), "Cancelled CLI extraction cleans staging");
        var corrupt = Path.Combine(root, "broken.7z"); await File.WriteAllBytesAsync(corrupt, [1, 2, 3]);
        await Reject(async () => { await using var input = File.OpenRead(corrupt); await using var archive = await FileArchiveService.OpenReaderAsync(input, corrupt, default); }, "Corrupt CLI input accepted");
        Check(!FileArchiveFormats.CanCreate(FileArchiveFormat.Rar) && FileArchiveFormats.IsBcl(FileArchiveFormat.Zip), "Read/create capabilities remain distinct");
        Console.WriteLine($"Archive provider checks passed: {checks}");
    }
}
