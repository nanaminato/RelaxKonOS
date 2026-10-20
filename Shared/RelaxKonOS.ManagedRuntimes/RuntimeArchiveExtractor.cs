using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes;

public sealed record VerifiedRuntimeArchive(string Directory, string EntryPoint);

/// <summary>
/// Extracts only signed, individually hashed files into a new private child of a host-protected root.
/// Does not execute code, preserve archive permissions, or activate a runtime. Caller holds the instance lock.
/// </summary>
public static class RuntimeArchiveExtractor
{
    public static void Discard(VerifiedRuntimeArchive archive, string protectedStagingRoot)
    {
        var root = Path.GetFullPath(protectedStagingRoot).TrimEnd(Path.DirectorySeparatorChar);
        var stage = Path.GetFullPath(archive.Directory);
        if (!string.Equals(Path.GetDirectoryName(stage), root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !Guid.TryParseExact(Path.GetFileName(stage), "N", out _))
            throw new RuntimePolicyException("runtime.staging_path_invalid");
        Cleanup(root, stage);
    }

    public static async Task<VerifiedRuntimeArchive> ExtractAsync(Stream archive, RuntimeArtifact artifact,
        string protectedStagingRoot, CancellationToken ct)
    {
        if (!archive.CanRead || !archive.CanSeek || !RuntimeArchiveRules.Valid(artifact)
            || artifact.Format is not ("zip" or "tar.gz" or "gz") || artifact.Sha256 is not { Length: 64 }
            || !artifact.Sha256.All(Uri.IsHexDigit) || artifact.MaximumBytes is <= 0 or > 2L * 1024 * 1024 * 1024)
            throw new RuntimePolicyException("runtime.artifact_invalid");
        var root = Path.GetFullPath(protectedStagingRoot);
        RuntimeSourceStore.RequireNoLinks(root);
        if (!System.IO.Directory.Exists(root)) throw new RuntimePolicyException("runtime.staging_root_missing");
        await VerifyArchiveAsync(archive, artifact, ct);
        var stage = Path.Combine(root, Guid.NewGuid().ToString("N"));
        RuntimeSourceStore.RequireNoLinks(stage);
        if (System.IO.Directory.Exists(stage) || File.Exists(stage)) throw new RuntimePolicyException("runtime.staging_conflict");
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(stage);
        else System.IO.Directory.CreateDirectory(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var extraction = new Extraction(stage, artifact, ct);
            if (artifact.Format == "zip") await extraction.ZipAsync(archive);
            else if (artifact.Format == "gz") await extraction.GzipAsync(archive);
            else await extraction.TarGzipAsync(archive);
            extraction.RequireComplete();
            return new(stage, Path.Combine(stage, artifact.EntryPoint.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException)
        {
            Cleanup(root, stage);
            throw new RuntimePolicyException("runtime.archive_invalid");
        }
        catch { Cleanup(root, stage); throw; }
    }

    private static async Task VerifyArchiveAsync(Stream archive, RuntimeArtifact artifact, CancellationToken ct)
    {
        archive.Position = 0;
        if (archive.Length > artifact.MaximumBytes) throw new RuntimePolicyException("runtime.archive_too_large");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await archive.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > artifact.MaximumBytes) throw new RuntimePolicyException("runtime.archive_too_large");
            digest.AppendData(buffer, 0, read);
        }
        if (!CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(artifact.Sha256)))
            throw new RuntimePolicyException("runtime.archive_digest_mismatch");
        archive.Position = 0;
    }

    private static void Cleanup(string root, string stage)
    {
        var canonical = Path.GetFullPath(stage);
        if (!canonical.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new RuntimePolicyException("runtime.staging_path_invalid");
        RuntimeSourceStore.RequireNoLinks(canonical);
        // Extraction never creates links, and the parent must be protected against other writers.
        System.IO.Directory.Delete(canonical, recursive: true);
    }

    private sealed class Extraction(string directory, RuntimeArtifact artifact, CancellationToken ct)
    {
        private readonly Dictionary<string, RuntimeArchiveFile> files = artifact.Layout.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        private readonly HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> completed = new(StringComparer.Ordinal);
        private readonly HashSet<string> directories = Ancestors(artifact.Layout.Files);
        private int entries;

        internal void RequireComplete()
        {
            if (completed.Count != files.Count) throw new RuntimePolicyException("runtime.archive_file_missing");
        }

        private RuntimeArchiveFile? Entry(string name, bool isDirectory)
        {
            ct.ThrowIfCancellationRequested();
            if (++entries > artifact.Layout.MaximumEntries) throw new RuntimePolicyException("runtime.archive_entry_limit");
            var path = isDirectory && name.EndsWith('/') ? name[..^1] : name;
            if (!RuntimeArchiveRules.SafePath(path) || !isDirectory && name.EndsWith('/'))
                throw new RuntimePolicyException("runtime.archive_path_invalid");
            if (!seen.Add(path)) throw new RuntimePolicyException("runtime.archive_duplicate_entry");
            if (isDirectory)
            {
                if (!directories.Contains(path)) throw new RuntimePolicyException("runtime.archive_file_undeclared");
                return null;
            }
            return files.TryGetValue(path, out var file) ? file : throw new RuntimePolicyException("runtime.archive_file_undeclared");
        }

        private async Task WriteAsync(RuntimeArchiveFile file, Func<Memory<byte>, CancellationToken, ValueTask<int>> read, bool checkEnd)
        {
            var path = Path.GetFullPath(Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new RuntimePolicyException("runtime.archive_path_invalid");
            RuntimeSourceStore.RequireNoLinks(path);
            var parent = Path.GetDirectoryName(path)!;
            if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(parent);
            else System.IO.Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            RuntimeSourceStore.RequireNoLinks(parent);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                | (file.Executable ? UnixFileMode.UserExecute : 0);
            await using var output = new FileStream(path, options);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            var remaining = file.Length;
            while (remaining > 0)
            {
                var length = await read(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                if (length == 0) throw new RuntimePolicyException("runtime.archive_file_length_mismatch");
                digest.AppendData(buffer, 0, length);
                await output.WriteAsync(buffer.AsMemory(0, length), ct);
                remaining -= length;
            }
            if (checkEnd && await read(buffer.AsMemory(0, 1), ct) != 0)
                throw new RuntimePolicyException("runtime.archive_file_length_mismatch");
            if (!CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(file.Sha256)))
                throw new RuntimePolicyException("runtime.archive_file_digest_mismatch");
            output.Flush(true);
            completed.Add(file.Path);
        }

        internal async Task ZipAsync(Stream archive)
        {
            // Bound central-directory allocation before ZipArchive materializes its entry collection.
            CheckZipDirectory(archive, artifact.Layout.MaximumEntries);
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            var accepted = new List<(ZipArchiveEntry Entry, RuntimeArchiveFile File)>();
            foreach (var entry in zip.Entries)
            {
                var directoryEntry = entry.FullName.EndsWith('/');
                var mode = (entry.ExternalAttributes >> 16) & 0xf000;
                if ((entry.ExternalAttributes & 0x400) != 0 || mode != 0 && mode != (directoryEntry ? 0x4000 : 0x8000))
                    throw new RuntimePolicyException("runtime.archive_entry_unsupported");
                var file = Entry(entry.FullName, directoryEntry);
                if (file is null)
                {
                    if (entry.Length != 0) throw new RuntimePolicyException("runtime.archive_invalid");
                    continue;
                }
                if (entry.Length != file.Length) throw new RuntimePolicyException("runtime.archive_file_length_mismatch");
                accepted.Add((entry, file));
            }
            foreach (var (entry, file) in accepted)
            {
                await using var input = entry.Open();
                await WriteAsync(file, input.ReadAsync, checkEnd: true);
            }
        }

        internal async Task GzipAsync(Stream archive)
        {
            // A single-member gzip is not a container: it must inflate to exactly the one declared
            // executable and then stop. The signed layout already guarantees that shape, and the
            // declared length plus the end-of-stream check reject trailing or appended payloads.
            using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
            var declared = artifact.Layout.Files[0];
            var input = new BoundedTarInput(gzip, artifact.Layout.MaximumExpandedBytes);
            await WriteAsync(declared, input.ReadAsync, checkEnd: true);
        }

        internal async Task TarGzipAsync(Stream archive)
        {
            using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
            // Count all inflated bytes, including padding and metadata. No PAX/GNU extension allocation.
            var input = new BoundedTarInput(gzip, artifact.Layout.MaximumExpandedBytes
                + (long)artifact.Layout.MaximumEntries * 1024 + 10240);
            var header = new byte[512];
            while (true)
            {
                await input.ExactlyAsync(header, ct);
                if (header.All(b => b == 0))
                {
                    await input.ExactlyAsync(header, ct);
                    if (header.Any(b => b != 0)) throw new RuntimePolicyException("runtime.archive_invalid");
                    int tail;
                    while ((tail = await input.ReadAsync(header, ct)) != 0)
                        if (header.AsSpan(0, tail).ContainsAnyExcept((byte)0)) throw new RuntimePolicyException("runtime.archive_invalid");
                    break;
                }
                var checksum = Octal(header.AsSpan(148, 8));
                long actual = 0;
                for (var i = 0; i < 512; i++) actual += i is >= 148 and < 156 ? 32 : header[i];
                if (checksum != actual) throw new RuntimePolicyException("runtime.archive_invalid");
                var size = Octal(header.AsSpan(124, 12));
                var kind = header[156];
                if (kind is not (0 or (byte)'0' or (byte)'5') || Name(header.AsSpan(157, 100)).Length != 0)
                    throw new RuntimePolicyException("runtime.archive_entry_unsupported");
                var name = Name(header.AsSpan(0, 100));
                if (header.AsSpan(257, 6).SequenceEqual("ustar\0"u8))
                {
                    var prefix = Name(header.AsSpan(345, 155));
                    if (prefix.Length > 0) name = prefix + "/" + name;
                }
                else if (!header.AsSpan(257, 6).SequenceEqual("ustar "u8) && header.AsSpan(257, 6).ContainsAnyExcept((byte)0))
                    throw new RuntimePolicyException("runtime.archive_entry_unsupported");
                var file = Entry(name, kind == (byte)'5');
                if (file is null)
                {
                    if (size != 0) throw new RuntimePolicyException("runtime.archive_invalid");
                    continue;
                }
                if (size != file.Length) throw new RuntimePolicyException("runtime.archive_file_length_mismatch");
                await WriteAsync(file, input.ReadAsync, checkEnd: false);
                var padding = (int)((512 - size % 512) % 512);
                if (padding > 0) await input.ExactlyAsync(header.AsMemory(0, padding), ct);
            }
        }

        private static HashSet<string> Ancestors(IReadOnlyList<RuntimeArchiveFile> files)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
                for (var slash = file.Path.IndexOf('/'); slash >= 0; slash = file.Path.IndexOf('/', slash + 1)) result.Add(file.Path[..slash]);
            return result;
        }
    }

    private sealed class BoundedTarInput(Stream stream, long limit)
    {
        private long total;
        internal async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if ((total += read) > limit) throw new RuntimePolicyException("runtime.archive_expansion_limit");
            return read;
        }
        internal async Task ExactlyAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await ReadAsync(buffer[offset..], ct);
                if (read == 0) throw new RuntimePolicyException("runtime.archive_invalid");
                offset += read;
            }
        }
    }

    private static string Name(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        if (end >= 0)
        {
            if (field[end..].ContainsAnyExcept((byte)0)) throw new RuntimePolicyException("runtime.archive_invalid");
            field = field[..end];
        }
        if (field.ContainsAnyInRange((byte)128, byte.MaxValue)) throw new RuntimePolicyException("runtime.archive_path_invalid");
        return Encoding.ASCII.GetString(field);
    }

    private static long Octal(ReadOnlySpan<byte> field)
    {
        long result = 0;
        var ended = false;
        foreach (var b in field)
        {
            if (b is 0 or 32) { if (result != 0) ended = true; continue; }
            if (ended || b is < (byte)'0' or > (byte)'7') throw new RuntimePolicyException("runtime.archive_invalid");
            result = checked(result * 8 + b - '0');
        }
        return result;
    }

    private static void CheckZipDirectory(Stream archive, int maximumEntries)
    {
        var tail = new byte[(int)Math.Min(archive.Length, 65557)];
        archive.Position = archive.Length - tail.Length;
        archive.ReadExactly(tail);
        var found = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length) { found = i; break; }
        if (found < 0) throw new RuntimePolicyException("runtime.archive_invalid");
        var entries = U16(tail, found + 10);
        if (U16(tail, found + 4) != 0 || U16(tail, found + 6) != 0 || entries == ushort.MaxValue)
            throw new RuntimePolicyException("runtime.archive_entry_unsupported");
        if (entries > maximumEntries) throw new RuntimePolicyException("runtime.archive_entry_limit");
        var size = U32(tail, found + 12);
        var start = U32(tail, found + 16);
        if (entries != U16(tail, found + 8) || (long)start + size != archive.Length - tail.Length + found)
            throw new RuntimePolicyException("runtime.archive_invalid");
        archive.Position = start;
        var header = new byte[46];
        var offsets = new HashSet<uint>();
        for (var i = 0; i < entries; i++)
        {
            archive.ReadExactly(header);
            if (U32(header, 0) != 0x02014b50 || U16(header, 34) != 0)
                throw new RuntimePolicyException("runtime.archive_invalid");
            if (U16(header, 6) > 20 || (U16(header, 8) & 0x2041) != 0 || U16(header, 10) is not (0 or 8))
                throw new RuntimePolicyException("runtime.archive_entry_unsupported");
            var name = U16(header, 28); var extra = U16(header, 30); var comment = U16(header, 32);
            var offset = U32(header, 42);
            if (name == 0 || name > 512 || extra > 4096 || comment > 1024 || offset >= start || !offsets.Add(offset))
                throw new RuntimePolicyException("runtime.archive_invalid");
            archive.Position += name + extra + comment;
            if (archive.Position > (long)start + size) throw new RuntimePolicyException("runtime.archive_invalid");
        }
        if (archive.Position != (long)start + size) throw new RuntimePolicyException("runtime.archive_invalid");
        archive.Position = 0;
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
}
