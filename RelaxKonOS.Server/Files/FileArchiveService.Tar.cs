using System.Formats.Tar;
using System.IO.Compression;

namespace RelaxKonOS.Server.Files;

public static partial class FileArchiveService
{
    private static async Task WriteTarAsync(ArchiveFileSystem fs, Stream output,
        IReadOnlyList<(string Path, string Name, bool Directory, long Size)> plan,
        RelaxKonOS.Protocol.Files.FileArchiveFormat format, Action<string, long, long, bool> report, CancellationToken ct)
    {
        await using var gzip = format == RelaxKonOS.Protocol.Files.FileArchiveFormat.TarGZip
            ? new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true) : null;
        await using var writer = new TarWriter(gzip ?? output, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var item in plan)
        {
            ct.ThrowIfCancellationRequested();
            var entry = new PaxTarEntry(item.Directory ? TarEntryType.Directory : TarEntryType.RegularFile, item.Name);
            if (item.Directory) await writer.WriteEntryAsync(entry, ct);
            else
            {
                await RejectLinksAsync(fs, item.Path, ct);
                await using var source = await fs.Read(item.Path, ct);
                if (source.Length != item.Size) throw new IOException("Source changed while archiving.");
                using var measured = new MeasuredTarInput(source, item.Size, bytes => report(item.Path, bytes, item.Size, false), ct);
                entry.DataStream = measured;
                await writer.WriteEntryAsync(entry, ct);
                if (measured.Position != item.Size) throw new IOException("Source changed while archiving.");
            }
            report(item.Path, item.Size, item.Size, true);
        }
    }

    private static async Task ExtractTarAsync(ArchiveFileSystem fs, Stream input, string staging,
        RelaxKonOS.Protocol.Files.FileArchiveFormat format, Action<string, long, long, bool> report, CancellationToken ct)
    {
        byte[]? footer = null;
        if (format == RelaxKonOS.Protocol.Files.FileArchiveFormat.TarGZip)
        {
            if (input.Length < 18) throw new InvalidDataException("GZip stream is truncated.");
            footer = new byte[8]; input.Position = input.Length - 8;
            await input.ReadExactlyAsync(footer, ct); input.Position = 0;
        }
        await using var gzip = format == RelaxKonOS.Protocol.Files.FileArchiveFormat.TarGZip
            ? new GZipStream(input, CompressionMode.Decompress, leaveOpen: true) : null;
        using var guarded = new TarFramingStream(gzip ?? input, input.Length, ct);
        await using var reader = new TarReader(guarded, leaveOpen: true);
        var entries = new Dictionary<string, bool>(Names);
        var directories = new HashSet<string>(StringComparer.Ordinal) { staging };
        long total = 0; int count = 0;
        async Task EnsureDirectory(string path)
        {
            if (!directories.Add(path)) return;
            await EnsureDirectory(Path.GetDirectoryName(path)!);
            await RejectLinksAsync(fs, path, ct);
            await fs.CreateDirectory(path, ct);
        }
        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken: ct)) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.EntryType == TarEntryType.GlobalExtendedAttributes) continue;
            if (entry is PaxTarEntry pax && pax.ExtendedAttributes.Keys.Any(key => key.StartsWith("GNU.sparse", StringComparison.Ordinal)))
                throw new InvalidDataException("Sparse TAR entries are not supported.");
            var directory = entry.EntryType == TarEntryType.Directory;
            if (!directory && entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException("TAR links, sparse files and special entries are not supported.");
            var name = entry.Name;
            while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            if (directory && name is "" or ".") continue; // Conventional archive root directory.
            if (directory && name.EndsWith('/')) name = name[..^1];
            ValidateName(name);
            var key = name.Normalize(System.Text.NormalizationForm.FormC);
            if (++count > MaximumEntries || !entries.TryAdd(key, directory)) throw new InvalidDataException("Duplicate or excessive TAR entries.");
            for (var slash = key.IndexOf('/'); slash >= 0; slash = key.IndexOf('/', slash + 1))
                if (entries.TryGetValue(key[..slash], out var parentDirectory) && !parentDirectory)
                    throw new InvalidDataException("A TAR file is also a parent directory.");
            if (!directory && entries.Keys.Any(existing => existing.StartsWith(key + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("A TAR file is also a parent directory.");
            if (entry.Length < 0 || entry.Length > MaximumEntryBytes || (total = checked(total + entry.Length)) > MaximumTotalBytes
                || directory && entry.Length != 0) throw new InvalidDataException("TAR expansion limit exceeded.");
            var target = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
            if (directory) await EnsureDirectory(target);
            else
            {
                await EnsureDirectory(Path.GetDirectoryName(target)!);
                await RejectLinksAsync(fs, target, ct);
                var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".entry.{Guid.NewGuid():N}.rkup");
                await fs.CreateFile(temporary, ct);
                await using (var output = new BufferedStream(new AppendStream(fs, temporary, ct, MaximumEntryBytes), 131072))
                {
                    if (entry.DataStream is { } content) await CopyAsync(content, output, entry.Length, bytes => report(name, bytes, entry.Length, false), ct);
                    else if (entry.Length != 0) throw new InvalidDataException("TAR content is missing.");
                }
                await fs.Move(temporary, target, ct);
            }
            report(name, entry.Length, entry.Length, true);
        }
        // Consume padding and GZip trailer so a corrupt checksum or appended archive cannot publish.
        var buffer = new byte[131072];
        while (await guarded.ReadAsync(buffer, ct) > 0) { }
        if (!guarded.SawEnd) throw new InvalidDataException("TAR end blocks are missing.");
        if (footer is not null && (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(footer) != guarded.Crc
            || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(4)) != unchecked((uint)guarded.DecodedBytes)))
            throw new InvalidDataException("GZip footer checksum or length mismatch.");
    }

    private sealed class MeasuredTarInput(Stream source, long length, Action<long> report, CancellationToken token) : Stream
    {
        private long position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set { if (value != position) throw new NotSupportedException(); } }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            token.ThrowIfCancellationRequested(); ct.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, length - position)], token);
            if (read == 0 && position < length) throw new EndOfStreamException("Source changed while archiving.");
            position += read; report(position); return read;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => offset == 0 && origin == SeekOrigin.Current ? position : throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Check raw TAR headers before TarReader can allocate PAX/GNU metadata or skip payloads.
    private sealed class TarFramingStream(Stream source, long compressedBytes, CancellationToken token) : Stream
    {
        private readonly byte[] header = new byte[512];
        private int headerPosition = 512;
        private long remaining, decoded, metadata;
        private int headers, endBlocks;
        private uint crc = uint.MaxValue;
        public bool SawEnd => endBlocks >= 2;
        public uint Crc => ~crc;
        public long DecodedBytes => decoded;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => decoded; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            token.ThrowIfCancellationRequested(); ct.ThrowIfCancellationRequested();
            if (buffer.IsEmpty) return 0;
            if (remaining == 0 && headerPosition == 512)
            {
                var read = await source.ReadAsync(header.AsMemory(0, 512), token);
                if (read == 0) return 0;
                await source.ReadExactlyAsync(header.AsMemory(read, 512 - read), token);
                if (header.All(value => value == 0)) { endBlocks++; remaining = 0; }
                else
                {
                    if (endBlocks != 0) throw new InvalidDataException("Unexpected data after TAR end blocks.");
                    if (++headers > MaximumEntries * 4) throw new InvalidDataException("Too many TAR records.");
                    // Positive GNU base-256 sizes within the ordinary file limit are supported.
                    ulong size = 0;
                    if ((header[124] & 0x80) != 0)
                    {
                        if (header[124] != 0x80) throw new InvalidDataException("Invalid TAR size.");
                        for (var i = 125; i < 136; i++) { if (size > (ulong)MaximumTotalBytes / 256) throw new InvalidDataException("TAR size limit exceeded."); size = size * 256 + header[i]; }
                    }
                    else
                    {
                        var text = System.Text.Encoding.ASCII.GetString(header, 124, 12).Trim('\0', ' ');
                        foreach (var digit in text) { if (digit is < '0' or > '7') throw new InvalidDataException("Invalid TAR size."); size = checked(size * 8 + (uint)(digit - '0')); }
                    }
                    var specialMetadata = header[156] is (byte)'x' or (byte)'g' or (byte)'L' or (byte)'K';
                    if (size > (ulong)MaximumEntryBytes || specialMetadata && (size > 1024 * 1024 || (metadata += (long)size) > MaximumMetadataBytes))
                        throw new InvalidDataException("TAR record limit exceeded.");
                    remaining = checked(((long)size + 511) / 512 * 512);
                }
                headerPosition = 0;
            }
            int count;
            if (headerPosition < 512)
            {
                count = Math.Min(buffer.Length, 512 - headerPosition);
                header.AsMemory(headerPosition, count).CopyTo(buffer); headerPosition += count;
            }
            else
            {
                count = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], token);
                if (count == 0) throw new EndOfStreamException("TAR payload is truncated.");
                remaining -= count;
            }
            for (var i = 0; i < count; i++) crc = CrcTable[(crc ^ buffer.Span[i]) & 255] ^ (crc >> 8);
            decoded = checked(decoded + count);
            if (decoded > MaximumTotalBytes + MaximumMetadataBytes + MaximumEntries * 4L * 512
                || decoded > 1024 * 1024 && decoded / Math.Max(1, compressedBytes) > 1000)
                throw new InvalidDataException("TAR expansion limit exceeded.");
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
