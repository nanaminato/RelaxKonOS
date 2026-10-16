namespace RelaxKonOS.Protocol.Files;

/// <summary>Formats implemented by the built-in archive backend; external filename conventions.</summary>
public enum FileArchiveFormat { Zip, Tar, TarGZip }

public static class FileArchiveFormats
{
    public static FileArchiveFormat? Detect(string name)
    {
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)) return FileArchiveFormat.TarGZip;
        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) return FileArchiveFormat.Tar;
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return FileArchiveFormat.Zip;
        return null;
    }

    public static string Extension(FileArchiveFormat format) => format switch
    {
        FileArchiveFormat.Zip => ".zip",
        FileArchiveFormat.Tar => ".tar",
        FileArchiveFormat.TarGZip => ".tar.gz",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string Stem(string name)
    {
        var format = Detect(name) ?? throw new ArgumentException("Unsupported archive format.");
        var length = format == FileArchiveFormat.TarGZip && name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? 4 : Extension(format).Length;
        var stem = name[..^length];
        return string.IsNullOrWhiteSpace(stem) ? "Archive" : stem;
    }
}
