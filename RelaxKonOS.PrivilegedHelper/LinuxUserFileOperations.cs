namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// File copy/move primitives used only after the Helper permanently assumes the authenticated
/// user's UID/GID. Copies are staged beside the destination so incomplete content is never
/// committed under the final name. Cross-filesystem moves fall back to staged copy then delete.
/// </summary>
public static class LinuxUserFileOperations
{
    private const int CrossDeviceLink = 18;

    public static void Copy(string source, string destination, bool overwrite)
    {
        if (!Exists(source)) throw new FileNotFoundException("Source path does not exist.", source);
        if (SamePath(source, destination)) return;
        var attributes = File.GetAttributes(source);
        var directory = attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint);
        if (directory && ContainsPath(source, destination))
            throw new ArgumentException("A directory cannot be copied into its own descendant.");
        if (Exists(destination) && !overwrite) throw new IOException("Destination already exists.");
        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            throw new DirectoryNotFoundException("Destination directory does not exist.");

        var temporary = Path.Combine(parent, $".relaxkonos-{Guid.NewGuid():N}.tmp");
        try
        {
            if (directory) CopyDirectory(source, temporary);
            else if (attributes.HasFlag(FileAttributes.ReparsePoint)) CopySymbolicLink(source, temporary, attributes);
            else File.Copy(source, temporary, overwrite: false);
            Commit(temporary, destination, overwrite);
        }
        finally
        {
            DeleteIfPresent(temporary);
        }
    }

    public static void Move(string source, string destination, bool overwrite)
    {
        if (!Exists(source)) throw new FileNotFoundException("Source path does not exist.", source);
        if (SamePath(source, destination)) return;
        var sourceAttributes = File.GetAttributes(source);
        var sourceIsDirectory = sourceAttributes.HasFlag(FileAttributes.Directory)
            && !sourceAttributes.HasFlag(FileAttributes.ReparsePoint);
        if (sourceIsDirectory && ContainsPath(source, destination))
            throw new ArgumentException("A directory cannot be moved into its own descendant.");
        var destinationExists = Exists(destination);
        if (destinationExists && !overwrite) throw new IOException("Destination already exists.");

        // Directory.Move cannot replace an existing tree. Stage first so the old destination
        // remains intact until the replacement is complete.
        if (sourceIsDirectory && destinationExists)
        {
            Copy(source, destination, overwrite: true);
            Directory.Delete(source, recursive: true);
            return;
        }

        try
        {
            if (sourceIsDirectory) Directory.Move(source, destination);
            else File.Move(source, destination, overwrite);
        }
        catch (IOException exception) when (IsCrossDevice(exception))
        {
            Copy(source, destination, overwrite);
            if (sourceIsDirectory) Directory.Delete(source, recursive: true);
            else File.Delete(source);
        }
    }

    private static void Commit(string staged, string destination, bool overwrite)
    {
        if (!Exists(destination))
        {
            MovePath(staged, destination);
            return;
        }
        if (!overwrite) throw new IOException("Destination already exists.");

        var parent = Path.GetDirectoryName(destination)!;
        var backup = Path.Combine(parent, $".relaxkonos-{Guid.NewGuid():N}.backup");
        MovePath(destination, backup);
        try
        {
            MovePath(staged, destination);
        }
        catch
        {
            try { if (!Exists(destination) && Exists(backup)) MovePath(backup, destination); } catch { }
            throw;
        }
        try { DeleteIfPresent(backup); }
        catch
        {
            // Cleanup is part of the commit. If the old tree cannot be removed, restore it so a
            // reported failure never leaves the caller observing the new destination anyway.
            try
            {
                if (Exists(destination)) MovePath(destination, staged);
                if (Exists(backup)) MovePath(backup, destination);
            }
            catch { }
            throw;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(entry));
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                CopySymbolicLink(entry, target, attributes);
            else if (attributes.HasFlag(FileAttributes.Directory))
                CopyDirectory(entry, target);
            else
                File.Copy(entry, target, overwrite: false);
        }
    }

    private static void CopySymbolicLink(string source, string destination, FileAttributes attributes)
    {
        var directory = attributes.HasFlag(FileAttributes.Directory);
        var target = directory ? new DirectoryInfo(source).LinkTarget : new FileInfo(source).LinkTarget;
        if (string.IsNullOrEmpty(target)) throw new IOException("Symbolic link target is unavailable.");
        if (directory) Directory.CreateSymbolicLink(destination, target);
        else File.CreateSymbolicLink(destination, target);
    }

    private static void MovePath(string source, string destination)
    {
        var attributes = File.GetAttributes(source);
        if (attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint))
            Directory.Move(source, destination);
        else File.Move(source, destination, overwrite: false);
    }

    private static void DeleteIfPresent(string path)
    {
        if (!Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint))
            Directory.Delete(path, recursive: true);
        else
            File.Delete(path);
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }
    private static bool IsCrossDevice(IOException exception) => (exception.HResult & 0xffff) == CrossDeviceLink;
    private static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.Ordinal);
    private static bool ContainsPath(string parent, string child)
    {
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var normalizedChild = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        return normalizedChild.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
