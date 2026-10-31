using RelaxKonOS.ModuleHost.Sdk;

namespace RelaxKonOS.ServerModuleHost;

/// <summary>Path-confined bounded storage for one ModuleHost process.</summary>
internal sealed class ModuleHostStorage : IModuleHostStorage
{
    private const int Limit = 4 * 1024 * 1024;
    private readonly string _root;
    private readonly StringComparison _comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal ModuleHostStorage(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute);
    }

    public async ValueTask<byte[]?> ReadAsync(string relativePath, int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes is < 1 or > Limit) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var path = Resolve(relativePath);
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length > maximumBytes || info.Length > Limit) throw new InvalidDataException("module-host-storage-size");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[checked((int)info.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }

    public async ValueTask WriteAsync(string relativePath, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        if (content.Length > Limit) throw new InvalidDataException("module-host-storage-size");
        var path = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous))
            {
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public bool Delete(string relativePath)
    {
        var path = Resolve(relativePath);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 240 || Path.IsPathFullyQualified(relativePath)
            || relativePath.Split('/', '\\').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("module-host-storage-path");
        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, _comparison))
            throw new InvalidDataException("module-host-storage-path");
        for (var current = path; current.Length >= _root.Length; current = Path.GetDirectoryName(current)!)
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("module-host-storage-path");
            if (string.Equals(current, _root, _comparison)) break;
        }
        return path;
    }
}
