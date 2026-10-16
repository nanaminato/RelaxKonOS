using System.Runtime.InteropServices;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Server.Files;

/// <summary>Used only where an existing file job already runs as the personal Unix user.</summary>
public sealed class LocalArchiveFileSystem : ArchiveFileSystem
{
    public override Task<FileSystemEntryDto?> Info(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return Task.FromResult<FileSystemEntryDto?>(null); }
        catch (DirectoryNotFoundException) { return Task.FromResult<FileSystemEntryDto?>(null); }
        var directory = attributes.HasFlag(FileAttributes.Directory);
        return Task.FromResult<FileSystemEntryDto?>(new(path, Path.GetFileName(path), directory ? null : new FileInfo(path).Length,
            directory ? FileSystemEntryType.Directory : FileSystemEntryType.File, null, null, null, false, false, null));
    }
    public override Task<FilePropertiesDto?> Properties(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(path);
        if (OperatingSystem.IsLinux())
        {
            if (Statx(-100, path, 0x100, 3, out var stat) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is 1 or 13) throw new UnauthorizedAccessException("Cannot inspect archive source type.");
                throw new IOException("Cannot inspect archive source type: " + error);
            }
            if ((stat.Mode & 0xf000) is not (0x4000 or 0x8000 or 0xa000)) attributes |= FileAttributes.Device;
        }
        return Task.FromResult<FilePropertiesDto?>(new(path, Path.GetFileName(path), attributes.HasFlag(FileAttributes.Directory) ? FileSystemEntryType.Directory : FileSystemEntryType.File,
            null, null, null, null, "", attributes.ToString(), null));
    }
    public override async Task<DirectoryDto> List(string path, CancellationToken ct)
    {
        var directories = new List<FileSystemEntryDto>(); var files = new List<FileEntryDto>();
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            var info = (await Info(child, ct))!;
            if (info.Type == FileSystemEntryType.Directory) directories.Add(info);
            else files.Add(new(child, info.Name, Path.GetExtension(child), info.Size ?? 0, null, null, null, false, false, "application/octet-stream"));
        }
        return new(path, Path.GetFileName(path), FileSystemEntryType.Directory, directories, files, null, null);
    }
    public override Task<Stream> Read(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true));
    }
    public override Task CreateDirectory(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Directory already exists.");
        Directory.CreateDirectory(path); return Task.CompletedTask;
    }
    public override async Task CreateFile(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }
    public override async Task<long> Append(string path, long offset, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 131072, true);
        if (file.Length != offset) throw new IOException("Archive append offset mismatch.");
        file.Position = offset;
        await file.WriteAsync(bytes, ct); await file.FlushAsync(ct); return file.Length;
    }
    public override Task Move(string source, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination, false);
        return Task.CompletedTask;
    }
    public override Task Delete(string path, CancellationToken ct)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path);
        return Task.CompletedTask;
    }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer { [FieldOffset(28)] public ushort Mode; }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out StatxBuffer stat);

}
