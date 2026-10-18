using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class ArchiveChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        var root = Path.Combine(Path.GetTempPath(), "relaxkonos-runtime-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var body = Encoding.UTF8.GetBytes("verified-runtime");
        var file = new RuntimeArchiveFile("bin/runtime", body.Length, Hash(body), true);
        var data = new (string Name, byte[] Bytes, bool Directory)[] { ("bin/", [], true), (file.Path, body, false) };
        RuntimeArtifact Artifact(byte[] bytes, string format) => new("artifact-1", Hash(bytes), bytes.Length,
            format, file.Path, "key-1", new(1024, 16, [file]));
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        async Task Reject(string problem, byte[] bytes, RuntimeArtifact artifact)
        {
            try
            {
                using var input = new MemoryStream(bytes);
                await RuntimeArchiveExtractor.ExtractAsync(input, artifact, root, default);
            }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime." + problem)
            {
                Check(!Directory.EnumerateFileSystemEntries(root).Any(), "Failed extraction cleans only its staging directory: " + problem);
                return;
            }
            throw new Exception("Expected archive rejection " + problem);
        }
        void RemoveStage(string path)
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Test cleanup escaped staging root");
            Directory.Delete(full, true);
        }
        try
        {
            foreach (var format in new[] { "zip", "tar.gz" })
            {
                byte[] Make((string Name, byte[] Bytes, bool Directory)[] entries) => format == "zip" ? Zip(entries) : Tar(entries);
                var bytes = Make(data);
                var artifact = Artifact(bytes, format);
                using (var input = new MemoryStream(bytes))
                {
                    var output = await RuntimeArchiveExtractor.ExtractAsync(input, artifact, root, default);
                    Check(File.ReadAllBytes(output.EntryPoint).SequenceEqual(body), "Declared file verified: " + format);
                    Check(Path.GetFileName(output.Directory).Length == 32 && Path.GetDirectoryName(output.Directory) == root,
                        "Host-generated isolated staging directory: " + format);
                    RemoveStage(output.Directory);
                }
                await Reject("archive_digest_mismatch", bytes, artifact with { Sha256 = new string('0', 64) });
                await Reject("archive_too_large", bytes, artifact with { MaximumBytes = bytes.Length - 1 });
                await Reject("archive_file_digest_mismatch", bytes, artifact with
                { Layout = artifact.Layout with { Files = [file with { Sha256 = new string('0', 64) }] } });
                await Reject("archive_file_length_mismatch", bytes, artifact with
                { Layout = artifact.Layout with { Files = [file with { Length = file.Length - 1 }] } });
                await Reject("archive_file_missing", bytes, artifact with
                { Layout = artifact.Layout with { Files = [file, new("LICENSE", 0, Hash([]), false)] } });
                var undeclared = Make([.. data, ("extra", body, false)]);
                await Reject("archive_file_undeclared", undeclared, Artifact(undeclared, format));
                var duplicate = Make([.. data, (file.Path, body, false)]);
                await Reject("archive_duplicate_entry", duplicate, Artifact(duplicate, format));
                var extraDirectory = Make([.. data, ("unlisted/", [], true)]);
                await Reject("archive_file_undeclared", extraDirectory, Artifact(extraDirectory, format));
                foreach (var path in new[] { "../escape", "/absolute", "C:/escape", "bin\\escape", "bin/./runtime", "bin//runtime", "CON", "file.", "bin/runtime:stream" })
                {
                    var unsafeBytes = Make([(path, body, false)]);
                    await Reject("archive_path_invalid", unsafeBytes, Artifact(unsafeBytes, format));
                }
                var countLimited = Make([.. data, ("another", body, false)]);
                await Reject("archive_entry_limit", countLimited,
                    Artifact(countLimited, format) with { Layout = new(1024, 2, [file]) });
                var truncated = bytes[..(bytes.Length / 2)];
                await Reject("archive_invalid", truncated, Artifact(truncated, format));
            }
            // Mihomo ships one raw gzip member per Linux build: the archive names no file at all, so the
            // signed layout may only declare the entrypoint itself.
            var gzFile = new RuntimeArchiveFile("mihomo", body.Length, Hash(body), true);
            RuntimeArtifact Gz(byte[] bytes) => new("artifact-gz", Hash(bytes), bytes.Length, "gz", gzFile.Path,
                "key-1", new(1024, 4, [gzFile]));
            var gz = Gzip(body);
            using (var input = new MemoryStream(gz))
            {
                var output = await RuntimeArchiveExtractor.ExtractAsync(input, Gz(gz), root, default);
                Check(File.ReadAllBytes(output.EntryPoint).SequenceEqual(body), "Single-member gzip extracts its one declared executable");
                Check(output.EntryPoint == Path.Combine(output.Directory, "mihomo"), "Single-member gzip entrypoint is exactly the declared path");
                RemoveStage(output.Directory);
            }
            await Reject("archive_file_digest_mismatch", gz, Gz(gz) with
            { Layout = new(1024, 4, [gzFile with { Sha256 = new string('0', 64) }]) });
            await Reject("archive_file_length_mismatch", gz, Gz(gz) with { Layout = new(1024, 4, [gzFile with { Length = gzFile.Length + 1 }]) });
            var appended = Gzip([.. body, 1, 2, 3]);
            await Reject("archive_file_length_mismatch", appended, Gz(appended));
            // A truncated gzip is refused either as an invalid stream or as an incomplete declared file;
            // which one depends on where deflate stops, and neither may ever be accepted.
            var truncatedGzip = gz[..(gz.Length / 2)];
            try
            {
                using var input = new MemoryStream(truncatedGzip);
                await RuntimeArchiveExtractor.ExtractAsync(input, Gz(truncatedGzip), root, default);
                throw new Exception("A truncated single-member gzip was extracted");
            }
            catch (RuntimePolicyException ex) when (ex.ProblemCode is "runtime.archive_invalid" or "runtime.archive_file_length_mismatch")
            { Check(!Directory.EnumerateFileSystemEntries(root).Any(), "A truncated single-member gzip leaves no extracted files"); }
            Check(!RuntimeArchiveRules.Valid(Gz(gz) with { Layout = new(1024, 4, [gzFile, new("LICENSE", 0, Hash([]), false)]) }),
                "A gzip artifact may not declare a second file");
            Check(!RuntimeArchiveRules.Valid(Gz(gz) with { Layout = new(1024, 4, [gzFile with { Path = "other" }]) }),
                "A gzip artifact must declare exactly its entrypoint");
            Check(!RuntimeArchiveRules.Valid(Gz(gz) with { Layout = new(1024, 4, [gzFile with { Length = 2048 }]) }),
                "A single gzip member is bounded by the signed expanded limit");
            Check(!RuntimeArchiveRules.Valid(Gz(gz) with { Format = "xz" }), "Unknown archive formats are rejected by the shared rules");
            var link = Zip([(file.Path, body, false)], symlink: true);
            await Reject("archive_entry_unsupported", link, Artifact(link, "zip"));
            foreach (var type in new[] { TarEntryType.SymbolicLink, TarEntryType.HardLink, TarEntryType.BlockDevice, TarEntryType.Fifo })
            {
                var special = SpecialTar(type, file.Path);
                await Reject("archive_entry_unsupported", special, Artifact(special, "tar.gz"));
            }
            var pax = PaxTar(file.Path, body);
            await Reject("archive_entry_unsupported", pax, Artifact(pax, "tar.gz"));
            var missingTrailer = TarRaw([(file.Path, body, false)]);
            missingTrailer = missingTrailer[..(512 + 512)]; // one header and padded body, no two zero records
            var compressed = Gzip(missingTrailer);
            await Reject("archive_invalid", compressed, Artifact(compressed, "tar.gz"));
            var hugeTail = Gzip(TarRaw(data).Concat(new byte[64000]).ToArray());
            await Reject("archive_expansion_limit", hugeTail, Artifact(hugeTail, "tar.gz"));
            var trailingPayload = Gzip(TarRaw(data).Concat(new byte[] { 1, 2, 3 }).ToArray());
            await Reject("archive_invalid", trailingPayload, Artifact(trailingPayload, "tar.gz"));
            var empty = Zip([]);
            await Reject("archive_file_missing", empty, Artifact(empty, "zip"));
            foreach (var unsafePath in new[] { "a/../b", "..", "a.", "NUL.txt", "COM1.exe", "Lpt9", "a~1/b", "a/b c", "a/%2e/b" })
                Check(!RuntimeArchiveRules.SafePath(unsafePath), "Unsafe signed declaration rejected: " + unsafePath);
            var baseline = Artifact(Zip(data), "zip");
            Check(!RuntimeArchiveRules.Valid(baseline with { Layout = new(1, 16, [file]) }), "Signed expanded limit is enforced");
            Check(!RuntimeArchiveRules.Valid(baseline with { Layout = new(1024, 16, [file, file with { Path = "BIN/RUNTIME" }]) }),
                "Case aliases in declarations rejected on every platform");
            Check(!RuntimeArchiveRules.Valid(baseline with { Layout = new(1024, 16, [file, file with { Path = "BIN/other" }]) }),
                "Case aliases of parent directories rejected even for distinct files");
            Check(!RuntimeArchiveRules.Valid(baseline with { Layout = new(1024, 16, [file, file with { Path = "bin" }]) }),
                "File cannot also be a parent directory");
            Check(!RuntimeArchiveRules.Valid(baseline with { Layout = new(1024, 16, [file with { Executable = false }]) }),
                "Entrypoint must be declared executable");
            var before = Path.Combine(root, "keep.txt");
            File.WriteAllText(before, "owned-by-caller");
            var bad = Zip(data);
            try
            {
                using var input = new MemoryStream(bad);
                await RuntimeArchiveExtractor.ExtractAsync(input, Artifact(bad, "zip") with { Sha256 = new string('0', 64) }, root, default);
                throw new Exception("Expected digest mismatch");
            }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.archive_digest_mismatch")
            { Check(File.ReadAllText(before) == "owned-by-caller", "Archive rejection does not delete caller-owned files"); }
            File.Delete(before);
            using var cancelledInput = new MemoryStream(bad);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await RuntimeArchiveExtractor.ExtractAsync(cancelledInput, Artifact(bad, "zip"), root, cancelled.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { Check(!Directory.EnumerateFileSystemEntries(root).Any(), "Cancellation leaves no extracted files"); }
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new Exception("Test root cleanup escaped temp");
            Directory.Delete(root, true);
        }
        return count;
    }

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    private static byte[] Zip((string Name, byte[] Bytes, bool Directory)[] entries, bool symlink = false)
    {
        using var output = new MemoryStream();
        using (var writer = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var entry in entries)
            {
                var item = writer.CreateEntry(entry.Name, CompressionLevel.SmallestSize);
                if (symlink) item.ExternalAttributes = unchecked((int)0xa1ff0000);
                using var data = item.Open(); data.Write(entry.Bytes);
            }
        return output.ToArray();
    }

    private static byte[] TarRaw((string Name, byte[] Bytes, bool Directory)[] entries)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, TarEntryFormat.Ustar, true))
            foreach (var entry in entries)
            {
                var item = new UstarTarEntry(entry.Directory ? TarEntryType.Directory : TarEntryType.RegularFile, entry.Name);
                if (!entry.Directory) item.DataStream = new MemoryStream(entry.Bytes);
                writer.WriteEntry(item);
                item.DataStream?.Dispose();
            }
        return output.ToArray();
    }
    private static byte[] Tar((string Name, byte[] Bytes, bool Directory)[] entries) => Gzip(TarRaw(entries));
    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, true)) gzip.Write(bytes);
        return output.ToArray();
    }
    private static byte[] SpecialTar(TarEntryType type, string path)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, TarEntryFormat.Ustar, true))
        {
            var entry = new UstarTarEntry(type, path);
            if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink) entry.LinkName = "../../outside";
            writer.WriteEntry(entry);
        }
        return Gzip(output.ToArray());
    }
    private static byte[] PaxTar(string path, byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, TarEntryFormat.Pax, true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(bytes) };
            writer.WriteEntry(entry);
        }
        return Gzip(output.ToArray());
    }
}
