namespace RelaxKonOS.Protocol.Files;

/// <summary>Formats recognized by the archive service; capabilities depend on the backend.</summary>
public enum FileArchiveFormat { Zip, Tar, TarGZip, SevenZip, Rar, Iso, Cab, Xz, BZip2, Zstd, Wim }

public static class FileArchiveFormats
{
    public static FileArchiveFormat? Detect(string name)
    {
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)) return FileArchiveFormat.TarGZip;
        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) return FileArchiveFormat.Tar;
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return FileArchiveFormat.Zip;
        foreach (var format in new[] { FileArchiveFormat.SevenZip, FileArchiveFormat.Rar, FileArchiveFormat.Iso, FileArchiveFormat.Cab, FileArchiveFormat.Xz, FileArchiveFormat.BZip2, FileArchiveFormat.Zstd, FileArchiveFormat.Wim })
            if (name.EndsWith(Extension(format), StringComparison.OrdinalIgnoreCase)) return format;
        return null;
    }

    public static string Extension(FileArchiveFormat format) => format switch
    {
        FileArchiveFormat.Zip => ".zip",
        FileArchiveFormat.Tar => ".tar",
        FileArchiveFormat.TarGZip => ".tar.gz",
        FileArchiveFormat.SevenZip => ".7z",
        FileArchiveFormat.Rar => ".rar",
        FileArchiveFormat.Iso => ".iso",
        FileArchiveFormat.Cab => ".cab",
        FileArchiveFormat.Xz => ".xz",
        FileArchiveFormat.BZip2 => ".bz2",
        FileArchiveFormat.Zstd => ".zst",
        FileArchiveFormat.Wim => ".wim",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static bool IsBcl(FileArchiveFormat format) => format is FileArchiveFormat.Zip or FileArchiveFormat.Tar or FileArchiveFormat.TarGZip;
    public static bool CanCreate(FileArchiveFormat format) => IsBcl(format);

    public static string Stem(string name)
    {
        var format = Detect(name) ?? throw new ArgumentException("Unsupported archive format.");
        var length = format == FileArchiveFormat.TarGZip && name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? 4 : Extension(format).Length;
        var stem = name[..^length];
        return string.IsNullOrWhiteSpace(stem) ? "Archive" : stem;
    }
}
