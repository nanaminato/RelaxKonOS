using System.IO.Compression;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Server.Files;

/// <summary>All filesystem access is supplied by the job's authenticated identity adapter.</summary>
public abstract class ArchiveFileSystem
{
    public abstract Task<FileSystemEntryDto?> Info(string path, CancellationToken ct);
    public abstract Task<FilePropertiesDto?> Properties(string path, CancellationToken ct);
    public abstract Task<DirectoryDto> List(string path, CancellationToken ct);
    public abstract Task<Stream> Read(string path, CancellationToken ct);
    public abstract Task CreateDirectory(string path, CancellationToken ct);
    public abstract Task CreateFile(string path, CancellationToken ct);
    public abstract Task<long> Append(string path, long offset, ReadOnlyMemory<byte> bytes, CancellationToken ct);
    public abstract Task Move(string source, string destination, CancellationToken ct);
    public abstract Task Delete(string path, CancellationToken ct);
}

public static class FileArchiveService
{
    public const int MaximumEntries = 10_000;
    public const long MaximumEntryBytes = 4L * 1024 * 1024 * 1024;
    public const long MaximumTotalBytes = 10L * 1024 * 1024 * 1024;
    public const long MaximumArchiveBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumMetadataBytes = 64L * 1024 * 1024;
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    public static async Task ExecuteAsync(ArchiveFileSystem fs, FileOperationKind kind,
        IReadOnlyList<string> sources, string destination, Action<string, long, long, bool> report,
        Action<string, Exception> cleanupFailed, CancellationToken ct)
    {
        if (kind is not (FileOperationKind.Compress or FileOperationKind.Extract)) throw new ArgumentException("Invalid archive operation.");
        await RejectLinksAsync(fs, destination, ct);
        if (await fs.Info(destination, ct) is not null) throw new IOException("Destination already exists.");
        var parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException("Destination needs a parent directory.");
        if ((await fs.Info(parent, ct))?.Type != FileSystemEntryType.Directory) throw new DirectoryNotFoundException(parent);
        var staging = Path.Combine(parent, kind == FileOperationKind.Compress ? $".archive.{Guid.NewGuid():N}.rkup" : $".relaxkonos-archive-{Guid.NewGuid():N}");
        var created = false;
        try
        {
            if (kind == FileOperationKind.Compress)
            {
                var plan = new List<(string Path, string Name, bool Directory, long Size)>();
                var names = new HashSet<string>(Names);
                long total = 0;
                async Task Add(string path, string name, int depth)
                {
                    ct.ThrowIfCancellationRequested();
                    if (depth > 128 || plan.Count >= MaximumEntries) throw new InvalidDataException("Archive selection is too large or too deep.");
                    ValidateName(name);
                    if (!names.Add(name.Normalize(System.Text.NormalizationForm.FormC))) throw new InvalidDataException("Duplicate archive entry names.");
                    await RejectLinksAsync(fs, path, ct);
                    var info = await fs.Info(path, ct) ?? throw new FileNotFoundException(path);
                    var directory = info.Type == FileSystemEntryType.Directory;
                    if (!directory && info.Type != FileSystemEntryType.File) throw new NotSupportedException("Only ordinary files and directories can be archived.");
                    var size = directory ? 0 : info.Size ?? throw new IOException("Unknown source size.");
                    if (size < 0 || size > MaximumEntryBytes || (total = checked(total + size)) > MaximumTotalBytes)
                        throw new InvalidDataException("Archive size limit exceeded.");
                    plan.Add((path, name, directory, size));
                    if (!directory) return;
                    var children = await fs.List(path, ct);
                    foreach (var child in children.Directories.Select(x => (x.Path, x.Name)).Concat(children.Files.Select(x => (x.Path, x.Name))))
                        await Add(child.Path, name + "/" + child.Name, depth + 1);
                }
                foreach (var source in sources) await Add(source, Path.GetFileName(source), 0);
                await fs.CreateFile(staging, ct); created = true;
                await using (var output = new BufferedStream(new AppendStream(fs, staging, ct, MaximumArchiveBytes), 131072))
                {
                    using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                    foreach (var item in plan)
                    {
                        ct.ThrowIfCancellationRequested();
                        var entry = zip.CreateEntry(item.Name + (item.Directory ? "/" : ""), CompressionLevel.Optimal);
                        if (!item.Directory)
                        {
                            await RejectLinksAsync(fs, item.Path, ct);
                            await using var input = await fs.Read(item.Path, ct);
                            await using var target = entry.Open();
                            await CopyAsync(input, target, item.Size, (bytes) => report(item.Path, bytes, item.Size, false), ct);
                        }
                        report(item.Path, item.Size, item.Size, true);
                    }
                }
            }
            else
            {
                if (sources.Count != 1) throw new ArgumentException("Extraction requires one ZIP archive.");
                await RejectLinksAsync(fs, sources[0], ct);
                await using var input = await fs.Read(sources[0], ct);
                if (!input.CanSeek || input.Length > MaximumArchiveBytes) throw new InvalidDataException("ZIP input is too large or not seekable.");
                await ValidateDirectoryBoundsAsync(input, ct);
                input.Position = 0;
                using var zip = new ZipArchive(input, ZipArchiveMode.Read);
                if (zip.Entries.Count > MaximumEntries) throw new InvalidDataException("Too many archive entries.");
                var entries = new Dictionary<string, bool>(Names);
                var plan = new List<(ZipArchiveEntry Entry, string Name, bool Directory)>();
                long total = 0;
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var directory = entry.FullName.EndsWith('/');
                    var name = directory ? entry.FullName[..^1] : entry.FullName;
                    ValidateName(name);
                    var type = (entry.ExternalAttributes >> 16) & 0xf000;
                    if (type != 0 && type != (directory ? 0x4000 : 0x8000)
                        || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Links and special archive entries are not supported.");
                    if (!entries.TryAdd(name.Normalize(System.Text.NormalizationForm.FormC), directory)) throw new InvalidDataException("Duplicate archive entries.");
                    if (entry.Length < 0 || entry.Length > MaximumEntryBytes || (total = checked(total + entry.Length)) > MaximumTotalBytes
                        || directory && entry.Length != 0 || entry.Length > 1024 * 1024 && entry.Length / Math.Max(1, entry.CompressedLength) > 1000)
                        throw new InvalidDataException("Archive expansion limit exceeded.");
                    plan.Add((entry, name, directory));
                }
                foreach (var name in entries.Keys)
                {
                    for (var slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
                        if (entries.TryGetValue(name[..slash], out var directory) && !directory)
                            throw new InvalidDataException("An archive file is also a parent directory.");
                }
                await fs.CreateDirectory(staging, ct); created = true;
                var directories = new HashSet<string>(Names) { staging };
                async Task EnsureDirectory(string path)
                {
                    if (!directories.Add(path)) return;
                    var ancestor = Path.GetDirectoryName(path)!;
                    await EnsureDirectory(ancestor);
                    await RejectLinksAsync(fs, path, ct);
                    await fs.CreateDirectory(path, ct);
                }
                foreach (var item in plan)
                {
                    ct.ThrowIfCancellationRequested();
                    var target = Path.Combine(staging, item.Name.Replace('/', Path.DirectorySeparatorChar));
                    if (item.Directory) await EnsureDirectory(target);
                    else
                    {
                        await EnsureDirectory(Path.GetDirectoryName(target)!);
                        await RejectLinksAsync(fs, target, ct);
                        var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".entry.{Guid.NewGuid():N}.rkup");
                        await fs.CreateFile(temporary, ct);
                        await using (var output = new BufferedStream(new AppendStream(fs, temporary, ct, MaximumEntryBytes), 131072))
                        await using (var content = item.Entry.Open())
                        {
                            var crc = await CopyAsync(content, output, item.Entry.Length,
                                bytes => report(item.Name, bytes, item.Entry.Length, false), ct);
                            if (crc != item.Entry.Crc32) throw new InvalidDataException("ZIP content checksum mismatch.");
                        }
                        await fs.Move(temporary, target, ct);
                    }
                    report(item.Name, item.Entry.Length, item.Entry.Length, true);
                }
            }
            ct.ThrowIfCancellationRequested();
            await RejectLinksAsync(fs, destination, ct);
            await fs.Move(staging, destination, ct);
            created = false;
        }
        finally
        {
            if (created)
                try { await fs.Delete(staging, CancellationToken.None); }
                catch (Exception ex) { cleanupFailed(staging, ex); }
        }
    }

    // Bound the central directory before BCL materializes its entries, including ZIP64.
    private static async Task ValidateDirectoryBoundsAsync(Stream input, CancellationToken ct)
    {
        if (input.Length < 22) throw new InvalidDataException("ZIP end record is missing.");
        var tail = new byte[(int)Math.Min(input.Length, 65557)];
        input.Position = input.Length - tail.Length;
        await input.ReadExactlyAsync(tail, ct);
        var offset = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50
                && i + 22 + System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) == tail.Length)
            { offset = i; break; }
        if (offset < 0) throw new InvalidDataException("Invalid ZIP end record.");
        var record = tail.AsMemory(offset);
        ushort U16(int i) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(record.Span[i..]);
        uint U32(int i) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(record.Span[i..]);
        if (U16(4) != 0 || U16(6) != 0 || U16(8) != U16(10)) throw new InvalidDataException("Split archives are not supported.");
        ulong count = U16(10), size = U32(12), start = U32(16);
        var endPosition = input.Length - tail.Length + offset;
        if (count == ushort.MaxValue || size == uint.MaxValue || start == uint.MaxValue)
        {
            if (endPosition < 76) throw new InvalidDataException("ZIP64 locator is missing.");
            var locator = new byte[20]; input.Position = endPosition - 20; await input.ReadExactlyAsync(locator, ct);
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064b50
                || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4)) != 0
                || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16)) != 1)
                throw new InvalidDataException("Invalid ZIP64 locator.");
            var position = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
            if (position > (ulong)(endPosition - 20 - 56)) throw new InvalidDataException("Invalid ZIP64 record offset.");
            var zip64 = new byte[56]; input.Position = (long)position; await input.ReadExactlyAsync(zip64, ct);
            ulong U64(int i) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(i));
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(zip64) != 0x06064b50 || U64(4) < 44
                || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(zip64.AsSpan(16)) != 0
                || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(zip64.AsSpan(20)) != 0 || U64(24) != U64(32))
                throw new InvalidDataException("Invalid ZIP64 end record.");
            count = U64(32); size = U64(40); start = U64(48);
        }
        if (count > MaximumEntries || size > MaximumMetadataBytes || start > (ulong)endPosition || size > (ulong)endPosition - start)
            throw new InvalidDataException("ZIP central directory exceeds archive limits.");
    }

    public static void ValidateName(string name)
    {
        if (name.Length > 4096 || name.Contains('\\') || name.Split('/').Length > 129)
            throw new InvalidDataException("Invalid archive entry path.");
        foreach (var part in name.Split('/'))
        {
            if (string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 || part.Any(char.IsControl)
                || System.Text.Encoding.UTF8.GetByteCount(part) > 255
                || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³" }.Contains(part.Split('.')[0].TrimEnd(' '), Names))
                throw new InvalidDataException("Unsafe archive entry name: " + name);
        }
    }

    private static async Task RejectLinksAsync(ArchiveFileSystem fs, string path, CancellationToken ct)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            var info = await fs.Info(current, ct);
            if (info is null) continue;
            var properties = await fs.Properties(current, ct) ?? throw new IOException("Cannot inspect archive path.");
            if (!Enum.TryParse<FileAttributes>(properties.Attributes, out var attributes)) throw new IOException("Invalid archive path metadata.");
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new NotSupportedException("Links and special files are not supported by archive jobs.");
        }
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        var crc = (uint)value;
        for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        return crc;
    }).ToArray();

    private static async Task<uint> CopyAsync(Stream input, Stream output, long expected, Action<long> report, CancellationToken ct)
    {
        var buffer = new byte[131072];
        long bytes = 0; uint crc = uint.MaxValue;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            bytes = checked(bytes + read);
            if (bytes > expected) throw new InvalidDataException("Archive content exceeds its declared length.");
            for (var i = 0; i < read; i++) crc = CrcTable[(crc ^ buffer[i]) & 255] ^ (crc >> 8);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            report(bytes);
        }
        if (bytes != expected) throw new InvalidDataException("Archive content is truncated or the source changed.");
        return ~crc;
    }

    private sealed class AppendStream(ArchiveFileSystem fs, string path, CancellationToken token, long maximumBytes) : Stream
    {
        private long position;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => position;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask().GetAwaiter().GetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            token.ThrowIfCancellationRequested(); ct.ThrowIfCancellationRequested();
            while (!buffer.IsEmpty)
            {
                var chunk = buffer[..Math.Min(buffer.Length, 131072)];
                if (position > maximumBytes - chunk.Length) throw new InvalidDataException("Archive output size limit exceeded.");
                var length = await fs.Append(path, position, chunk, token);
                if (length != position + chunk.Length) throw new IOException("Archive append length mismatch.");
                position = length; buffer = buffer[chunk.Length..];
            }
        }
        public override void Flush() => token.ThrowIfCancellationRequested();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
