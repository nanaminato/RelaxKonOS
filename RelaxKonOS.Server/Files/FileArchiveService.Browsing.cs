using System.IO.Compression;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Server.Files;

public static partial class FileArchiveService
{
    public static async Task<IArchiveReader> OpenReaderAsync(Stream input, string fileName, CancellationToken ct, ArchiveCredentials? credentials = null)
    {
        credentials?.Validate();
        var format = FileArchiveFormats.Detect(fileName) ?? throw new NotSupportedException("Unsupported archive format.");
        if (credentials is not null && !FileArchiveFormats.CanEncrypt(format) && format != FileArchiveFormat.Rar)
            throw new ArgumentException("This archive format does not support passwords.");
        if (!input.CanSeek || input.Length > MaximumArchiveBytes) throw new InvalidDataException("Archive input is too large or not seekable.");
        return FileArchiveFormats.IsBcl(format)
            ? await BclArchiveProvider.OpenAsync(input, format, fileName, ct, credentials)
            : await SevenZipArchiveProvider.OpenAsync(input, format, ct, fileName, credentials);
    }

    public static async Task<byte[]> ReadPreviewAsync(IArchiveReader archive, string name, CancellationToken ct)
    {
        var entry = archive.Entries.SingleOrDefault(e => e.Name == name && !e.IsDirectory)
            ?? throw new FileNotFoundException("Archive entry not found.");
        if (entry.Size > ArchivePreviewLimits.MaximumBytes) throw new NotSupportedException("Archive preview is limited to 16 MiB per file.");
        using var output = new MemoryStream((int)entry.Size);
        await archive.CopyEntryAsync(name, output, ct);
        return output.ToArray();
    }

    private static async Task ExtractProviderAsync(ArchiveFileSystem fs, IArchiveReader archive, string staging,
        Action<string, long, long, bool> report, CancellationToken ct)
    {
        var directories = new HashSet<string>(Names) { staging };
        async Task EnsureDirectory(string path)
        {
            if (!directories.Add(path)) return;
            await EnsureDirectory(Path.GetDirectoryName(path)!);
            await RejectLinksAsync(fs, path, ct);
            await fs.CreateDirectory(path, ct);
        }
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(staging, entry.Name.Replace('/', Path.DirectorySeparatorChar));
            if (entry.IsDirectory) await EnsureDirectory(target);
            else
            {
                await EnsureDirectory(Path.GetDirectoryName(target)!);
                await RejectLinksAsync(fs, target, ct);
                var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".entry.{Guid.NewGuid():N}.rkup");
                await fs.CreateFile(temporary, ct);
                await using (var output = new BufferedStream(new AppendStream(fs, temporary, ct, entry.Size,
                    bytes => report(entry.Name, bytes, entry.Size, false)), 131072))
                    await archive.CopyEntryAsync(entry.Name, output, ct);
                await fs.Move(temporary, target, ct);
            }
            report(entry.Name, entry.Size, entry.Size, true);
        }
    }

    private sealed class BclArchiveProvider(Stream input, FileArchiveFormat format) : IArchiveReader
    {
        private ZipArchive? zip;
        public IReadOnlyList<ArchiveEntryDto> Entries { get; private set; } = [];

        public static async Task<IArchiveReader> OpenAsync(Stream input, FileArchiveFormat format, string fileName, CancellationToken ct,
            ArchiveCredentials? credentials)
        {
            var result = new BclArchiveProvider(input, format);
            try
            {
                var entries = new List<ArchiveEntryDto>();
                if (format == FileArchiveFormat.Zip)
                {
                    await ValidateDirectoryBoundsAsync(input, ct);
                    input.Position = 0;
                    result.zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
                    foreach (var entry in result.zip.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        var directory = entry.FullName.EndsWith('/');
                        var type = (entry.ExternalAttributes >> 16) & 0xf000;
                        if (type != 0 && type != (directory ? 0x4000 : 0x8000)
                            || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException("Links and special archive entries are not supported.");
                        if (entry.Length > 1024 * 1024 && entry.Length / Math.Max(1, entry.CompressedLength) > 1000)
                            throw new InvalidDataException("Archive expansion limit exceeded.");
                        entries.Add(new(directory ? entry.FullName[..^1] : entry.FullName, directory, entry.Length));
                    }
                }
                else
                {
                    input.Position = 0;
                    await ExtractTarAsync(null!, input, "", format, (_, _, _, _) => { }, ct,
                        (entry, name, directory) => { entries.Add(new(name, directory, entry.Length)); return Task.CompletedTask; });
                }
                ArchiveEntryValidation.Validate(entries);
                result.Entries = entries;
                if (format == FileArchiveFormat.Zip && await ZipNeedsCliAsync(input, ct))
                {
                    await result.DisposeAsync();
                    return await SevenZipArchiveProvider.OpenAsync(input, format, ct, fileName, credentials);
                }
                return result;
            }
            catch { await result.DisposeAsync(); throw; }
        }

        public async Task CopyEntryAsync(string name, Stream output, CancellationToken ct)
        {
            var entry = Entries.SingleOrDefault(e => e.Name == name && !e.IsDirectory)
                ?? throw new FileNotFoundException("Archive entry not found.");
            if (zip is not null)
            {
                var source = zip.GetEntry(name)!;
                await using var content = source.Open();
                if (await CopyAsync(content, output, entry.Size, _ => { }, ct) != source.Crc32)
                    throw new InvalidDataException("ZIP content checksum mismatch.");
            }
            else
            {
                input.Position = 0;
                await ExtractTarAsync(null!, input, "", format, (_, _, _, _) => { }, ct, async (item, itemName, directory) =>
                {
                    if (itemName == name && !directory && item.DataStream is { } content)
                        await CopyAsync(content, output, entry.Size, _ => { }, ct);
                });
            }
        }

        public ValueTask DisposeAsync() { zip?.Dispose(); return ValueTask.CompletedTask; }
    }

    private static async Task<bool> ZipNeedsCliAsync(Stream input, CancellationToken ct)
    {
        // Select by the actual central-directory method, never by a catch-all parse failure.
        var (start, count) = await ValidateDirectoryBoundsAsync(input, ct);
        input.Position = start;
        var header = new byte[46]; var needsCli = false;
        for (long index = 0; index < count; index++)
        {
            await input.ReadExactlyAsync(header, ct);
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50)
                throw new InvalidDataException("Invalid ZIP central directory.");
            ushort Field(int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(offset));
            needsCli |= (Field(8) & 1) != 0 || Field(10) is not (0 or 8 or 9);
            var skip = Field(28) + Field(30) + Field(32);
            if (input.Position > input.Length - skip) throw new InvalidDataException("Truncated ZIP central directory.");
            input.Position += skip;
        }
        return needsCli;
    }
}
