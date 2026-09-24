using System.Diagnostics;
using System.Globalization;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// File copy/move primitives used only after the Helper permanently assumes the authenticated
/// user's UID/GID. Copies are staged beside the destination so incomplete content is never
/// committed under the final name. Cross-filesystem moves fall back to staged copy then delete.
/// </summary>
public static class LinuxUserFileOperations
{
    private const int CrossDeviceLink = 18;
    private const string TransactionPrefix = ".relaxkonos-stage-v1-";
    private const string TransactionSuffix = ".tmp";
    private const string ManifestName = "manifest";
    private const string StagedName = "staged";
    private const string BackupName = "backup";

    /// <summary>
    /// Atomically replaces a file after its full content has been written beside the destination.
    /// An interrupted write therefore never exposes a partially-written final file. Any abandoned
    /// transaction for the same destination is recovered before the new write starts.
    /// </summary>
    public static void WriteAllBytes(string destination, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var transaction = BeginTransaction(destination);
        try
        {
            File.WriteAllBytes(transaction.Staged, content);
            if (OperatingSystem.IsLinux() && File.Exists(destination)
                && !File.GetAttributes(destination).HasFlag(FileAttributes.ReparsePoint))
                File.SetUnixFileMode(transaction.Staged, File.GetUnixFileMode(destination));
            Commit(transaction, overwrite: true);
        }
        finally
        {
            FinishTransaction(transaction);
        }
    }

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

        var transaction = BeginTransaction(destination);
        try
        {
            if (directory) CopyDirectory(source, transaction.Staged);
            else if (attributes.HasFlag(FileAttributes.ReparsePoint)) CopySymbolicLink(source, transaction.Staged, attributes);
            else File.Copy(source, transaction.Staged, overwrite: false);
            Commit(transaction, overwrite);
        }
        finally
        {
            FinishTransaction(transaction);
        }
    }

    /// <summary>
    /// Recovers transactions whose creating process is no longer alive. Directory enumeration
    /// invokes this after identity drop so a killed Helper does not permanently expose staging
    /// entries in Explorer. Live concurrent operations are left untouched.
    /// </summary>
    public static void RecoverAbandonedInDirectory(string directory)
        => RecoverAbandoned(directory, requiredDestination: null);

    private static void RecoverAbandoned(string directory, string? requiredDestination)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var candidate in Directory.EnumerateDirectories(directory, $"{TransactionPrefix}*{TransactionSuffix}",
                     SearchOption.TopDirectoryOnly))
        {
            if (!TryReadTransaction(candidate, out var transaction) || IsCreatingProcessAlive(transaction))
                continue;
            if (!FinishTransaction(transaction) && requiredDestination is not null
                && SamePath(transaction.Destination, requiredDestination))
                throw new IOException("An interrupted destination transaction could not be recovered.");
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

    private static void Commit(Transaction transaction, bool overwrite)
    {
        if (!Exists(transaction.Destination))
        {
            MovePath(transaction.Staged, transaction.Destination);
            return;
        }
        if (!overwrite) throw new IOException("Destination already exists.");

        MovePath(transaction.Destination, transaction.Backup);
        try
        {
            MovePath(transaction.Staged, transaction.Destination);
        }
        catch
        {
            try
            {
                if (!Exists(transaction.Destination) && Exists(transaction.Backup))
                    MovePath(transaction.Backup, transaction.Destination);
            }
            catch { }
            throw;
        }
        try { DeleteIfPresent(transaction.Backup); }
        catch
        {
            // Cleanup is part of the commit. If the old tree cannot be removed, restore it so a
            // reported failure never leaves the caller observing the new destination anyway.
            try
            {
                if (Exists(transaction.Destination)) MovePath(transaction.Destination, transaction.Staged);
                if (Exists(transaction.Backup)) MovePath(transaction.Backup, transaction.Destination);
            }
            catch { }
            throw;
        }
    }

    private static Transaction BeginTransaction(string destination)
    {
        destination = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            throw new DirectoryNotFoundException("Destination directory does not exist.");

        // Recovery of this exact destination is fail-closed. Starting a replacement while its old
        // backup cannot be restored could otherwise turn a recoverable interruption into data loss.
        RecoverAbandoned(parent, destination);
        var process = Process.GetCurrentProcess();
        var root = Path.Combine(parent, $"{TransactionPrefix}{Guid.NewGuid():N}{TransactionSuffix}");
        Directory.CreateDirectory(root);
        var transaction = new Transaction(root, destination, Environment.ProcessId,
            process.StartTime.ToUniversalTime().Ticks);
        try
        {
            // The manifest is intentionally small and contains no file content or credentials.
            // Flush it before staging begins so a later Helper can identify an interrupted owner.
            using var manifest = new FileStream(transaction.Manifest, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough);
            using var writer = new StreamWriter(manifest, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true);
            writer.WriteLine("1");
            writer.WriteLine(transaction.ProcessId.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine(transaction.ProcessStartUtcTicks.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(destination)));
            writer.Flush();
            manifest.Flush(flushToDisk: true);
            return transaction;
        }
        catch
        {
            DeleteIfPresent(root);
            throw;
        }
    }

    private static bool TryReadTransaction(string root, out Transaction transaction)
    {
        transaction = default;
        try
        {
            var attributes = File.GetAttributes(root);
            if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;
            var manifestPath = Path.Combine(root, ManifestName);
            if (new FileInfo(manifestPath).Length > 4096) return false;
            var lines = File.ReadAllLines(manifestPath);
            if (lines.Length != 4 || lines[0] != "1"
                || !int.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
                || processId <= 0
                || !long.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out var processStartUtcTicks)
                || processStartUtcTicks <= 0)
                return false;
            var destination = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(lines[3]));
            if (!Path.IsPathFullyQualified(destination)) return false;
            destination = Path.GetFullPath(destination);
            if (!SamePath(Path.GetDirectoryName(destination)!, Path.GetDirectoryName(root)!)) return false;
            transaction = new(root, destination, processId, processStartUtcTicks);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsCreatingProcessAlive(Transaction transaction)
    {
        try
        {
            using var process = Process.GetProcessById(transaction.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == transaction.ProcessStartUtcTicks;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static bool FinishTransaction(Transaction transaction)
    {
        try
        {
            if (Exists(transaction.Backup))
            {
                if (Exists(transaction.Destination)) DeleteIfPresent(transaction.Backup);
                else MovePath(transaction.Backup, transaction.Destination);
            }
            DeleteIfPresent(transaction.Staged);
            if (File.Exists(transaction.Manifest)) File.Delete(transaction.Manifest);
            if (Directory.Exists(transaction.Root)) Directory.Delete(transaction.Root, recursive: false);
            return true;
        }
        catch
        {
            // Leave a valid transaction behind. A later operation or directory listing under the
            // same user identity will retry recovery without ever exposing an incomplete target.
            return false;
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

    private readonly record struct Transaction(string Root, string Destination, int ProcessId,
        long ProcessStartUtcTicks)
    {
        public string Manifest => Path.Combine(Root, ManifestName);
        public string Staged => Path.Combine(Root, StagedName);
        public string Backup => Path.Combine(Root, BackupName);
    }
}
