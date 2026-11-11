using System.Formats.Tar;
using RelaxKonOS.Server.Files;

public static class TarArchiveChecks
{
    public static async Task RunAsync(string root)
    {
        var fs = new LocalArchiveFileSystem();
        var source = Path.Combine(root, "tar-source"); Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(source, "空目录"));
        File.WriteAllText(Path.Combine(source, "中文 😀.txt"), "TAR contents");
        File.WriteAllBytes(Path.Combine(source, "empty"), []);
        var count = 0;
        void Check(bool value, string text) { if (!value) throw new Exception(text); Console.WriteLine($"PASS TAR {++count}: {text}"); }
        Task Run(FileOperationKind kind, string input, string destination, Action<string, long, long, bool>? progress = null, CancellationToken ct = default) =>
            FileArchiveService.ExecuteAsync(fs, kind, [input], destination, progress ?? ((_, _, _, _) => { }), (_, error) => throw error, ct);
        foreach (var suffix in new[] { ".tar", ".tar.gz", ".tgz" })
        {
            var archive = Path.Combine(root, "roundtrip" + suffix); var output = Path.Combine(root, "result" + suffix);
            await Run(FileOperationKind.Compress, source, archive);
            await Run(FileOperationKind.Extract, archive, output);
            Check(File.ReadAllText(Path.Combine(output, "tar-source", "中文 😀.txt")) == "TAR contents"
                && Directory.Exists(Path.Combine(output, "tar-source", "空目录")) && new FileInfo(Path.Combine(output, "tar-source", "empty")).Length == 0,
                suffix + " roundtrip preserves Unicode, empty directories and empty files");
        }
        async Task Bad(string label, Action<TarWriter> write, Action<string>? mutate = null, bool gzip = false)
        {
            var archive = Path.Combine(root, "bad-tar" + (gzip ? ".tar.gz" : ".tar"));
            using (var output = File.Create(archive))
            using (var wrapped = gzip ? new GZipStream(output, CompressionLevel.Optimal, true) : null)
            using (var writer = new TarWriter((Stream?)wrapped ?? output, TarEntryFormat.Pax, true)) write(writer);
            mutate?.Invoke(archive);
            var target = Path.Combine(root, "bad-tar-output");
            try { await Run(FileOperationKind.Extract, archive, target); throw new Exception("Accepted " + label); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or NotSupportedException)
            { Check(!Directory.Exists(target), label + " fails without publishing a directory"); }
            File.Delete(archive);
        }
        foreach (var name in new[] { "../escape", "/absolute", "C:/escape", "a\\escape" })
            await Bad("TAR unsafe path " + name, writer => writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)));
        await Bad("TAR symbolic link", writer => writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "outside" }));
        await Bad("TAR hard link", writer => writer.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "hard") { LinkName = "target" }));
        await Bad("TAR duplicate names", writer => { writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "A")); writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "a")); });
        await Bad("TAR parent file collision", writer => { writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "a")); writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "a/b")); });
        await Bad("TAR reverse parent collision", writer => { writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "a/b")); writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "a")); });
        await Bad("Truncated TAR", writer => writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "payload") { DataStream = new MemoryStream(new byte[2048]) }),
            archive => { using var stream = new FileStream(archive, FileMode.Open, FileAccess.Write); stream.SetLength(1800); });
        await Bad("GZip checksum mismatch", writer => writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "data") { DataStream = new MemoryStream([1, 2, 3]) }),
            archive => { var bytes = File.ReadAllBytes(archive); bytes[^8] ^= 1; File.WriteAllBytes(archive, bytes); }, true);
        await Bad("Missing GZip footer", writer => writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "data") { DataStream = new MemoryStream([1, 2, 3]) }),
            archive => { using var stream = new FileStream(archive, FileMode.Open, FileAccess.Write); stream.SetLength(stream.Length - 8); }, true);
        await Bad("Nonzero data after TAR end", writer => writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "data")),
            archive => { using var stream = new FileStream(archive, FileMode.Append, FileAccess.Write); stream.Write(Enumerable.Repeat((byte)1, 512).ToArray()); });
        await Bad("Oversized PAX metadata header", writer => writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "data")),
            archive => { var bytes = File.ReadAllBytes(archive); Encoding.ASCII.GetBytes("00010000001\0").CopyTo(bytes, 124); bytes[156] = (byte)'x';
                Array.Fill(bytes, (byte)' ', 148, 8); var checksum = bytes.Take(512).Sum(value => value);
                Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ").CopyTo(bytes, 148); File.WriteAllBytes(archive, bytes); });
        var conventional = Path.Combine(root, "conventional.tar");
        using (var writer = new TarWriter(File.Create(conventional), TarEntryFormat.Ustar))
        { writer.WriteEntry(new UstarTarEntry(TarEntryType.Directory, "./")); writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "./hello") { DataStream = new MemoryStream([4, 5]) }); }
        await Run(FileOperationKind.Extract, conventional, Path.Combine(root, "conventional-result"));
        Check(File.ReadAllBytes(Path.Combine(root, "conventional-result", "hello")).SequenceEqual(new byte[] { 4, 5 }), "Conventional ./ TAR root and UStar entries are supported");
        using (var cancel = new CancellationTokenSource())
        {
            try { await Run(FileOperationKind.Extract, Path.Combine(root, "roundtrip.tar.gz"), Path.Combine(root, "cancel-tar"), (_, _, _, _) => cancel.Cancel(), cancel.Token); throw new Exception("Ignored TAR cancellation"); }
            catch (OperationCanceledException) { Check(!Directory.Exists(Path.Combine(root, "cancel-tar")), "TAR cancellation cleans all staged files"); }
        }
        Check(!Directory.EnumerateFileSystemEntries(root).Any(p => Path.GetFileName(p).StartsWith(".relaxkonos-archive-")), "TAR failures leave no staging directories");
    }
}
