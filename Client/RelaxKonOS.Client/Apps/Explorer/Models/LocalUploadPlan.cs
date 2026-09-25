namespace RelaxKonOS.Client.Apps.Explorer.Models;

public sealed record LocalUploadFile(string SourcePath, string RelativePath, long Length);
public sealed record LocalUploadPlan(IReadOnlyList<string> Directories, IReadOnlyList<LocalUploadFile> Files, long TotalBytes)
{
    public static LocalUploadPlan Build(IEnumerable<LocalUploadSource> sources)
    {
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<LocalUploadFile>();
        foreach (var source in sources.Select(item => item.Path).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal))
        {
            if (File.Exists(source))
            {
                var info = new FileInfo(source);
                files.Add(new(source, info.Name, info.Length));
                continue;
            }
            if (!Directory.Exists(source)) continue;

            var root = Path.TrimEndingDirectorySeparator(source);
            if (string.Equals(root, Path.GetPathRoot(root), OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
                throw new ArgumentException("Selecting a filesystem root for upload is not supported.");
            var parent = Path.GetDirectoryName(root) ?? root;
            directories.Add(Path.GetRelativePath(parent, root));
            foreach (var directory in Directory.EnumerateDirectories(root, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                     }))
                directories.Add(Path.GetRelativePath(parent, directory));

            foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                     }))
            {
                var info = new FileInfo(file);
                files.Add(new(file, Path.GetRelativePath(parent, file), info.Length));
            }
        }

        return new(directories.OrderBy(path => path.Length).ToArray(), files, files.Sum(file => file.Length));
    }

    public static string CombineRemotePath(string directory, string relativePath)
    {
        var result = directory;
        foreach (var segment in relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            result = ExplorerPath.Combine(result, segment);
        return result;
    }
}
