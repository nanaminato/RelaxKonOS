using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// File copy/move primitives used only after the Helper permanently assumes the authenticated
/// user's UID/GID. Copies are staged beside the destination so incomplete content is never
/// committed under the final name. Cross-filesystem moves fall back to staged copy then delete.
/// </summary>
public static class LinuxUserFileOperations
{
    private const int CrossDeviceLink = 18;
    private const int OpenReadOnly = 0;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int RemoveDirectory = 0x200;
    private const string TransactionPrefix = ".relaxkonos-stage-v1-";
    private const string TransactionSuffix = ".tmp";
    private const string ManifestName = "manifest";
    private const string PendingManifestName = "manifest.pending";
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
        using var transaction = BeginTransaction(destination);
        try
        {
            File.WriteAllBytes(transaction.Staged, content);
            if (OperatingSystem.IsLinux() && File.Exists(transaction.AnchoredDestination)
                && !File.GetAttributes(transaction.AnchoredDestination).HasFlag(FileAttributes.ReparsePoint))
                File.SetUnixFileMode(transaction.Staged,
                    File.GetUnixFileMode(transaction.AnchoredDestination));
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

        using var transaction = BeginTransaction(destination);
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
            if (!TryReadTransaction(candidate, out var transaction))
            {
                RecoverIncompleteInitialization(candidate);
                continue;
            }
            using (transaction)
            {
                if (IsCreatingProcessAlive(transaction)) continue;
                if (!FinishTransaction(transaction) && requiredDestination is not null
                    && SamePath(transaction.Destination, requiredDestination))
                    throw new IOException("An interrupted destination transaction could not be recovered.");
            }
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
        if (!Exists(transaction.AnchoredDestination))
        {
            RenameAt(transaction.RootHandle, StagedName, transaction.ParentHandle,
                transaction.DestinationName);
            return;
        }
        if (!overwrite) throw new IOException("Destination already exists.");

        RenameAt(transaction.ParentHandle, transaction.DestinationName, transaction.RootHandle,
            BackupName);
        try
        {
            RenameAt(transaction.RootHandle, StagedName, transaction.ParentHandle,
                transaction.DestinationName);
        }
        catch
        {
            try
            {
                if (!Exists(transaction.AnchoredDestination) && Exists(transaction.Backup))
                    RenameAt(transaction.RootHandle, BackupName, transaction.ParentHandle,
                        transaction.DestinationName);
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
                if (Exists(transaction.AnchoredDestination))
                    RenameAt(transaction.ParentHandle, transaction.DestinationName,
                        transaction.RootHandle, StagedName);
                if (Exists(transaction.Backup))
                    RenameAt(transaction.RootHandle, BackupName, transaction.ParentHandle,
                        transaction.DestinationName);
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
        using var process = Process.GetCurrentProcess();
        var processStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        var rootName = $"{TransactionPrefix}{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}-"
            + $"{processStartUtcTicks.ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}{TransactionSuffix}";
        var parentHandle = OpenDirectoryHandle(parent);
        if (mkdirat(Descriptor(parentHandle), rootName, Convert.ToUInt32("700", 8)) != 0)
        {
            var exception = NativeIOException("Could not create the user-execution transaction directory");
            parentHandle.Dispose();
            throw exception;
        }
        SafeFileHandle? rootHandle = null;
        try
        {
            rootHandle = OpenDirectoryHandleAt(parentHandle, rootName);
        }
        catch
        {
            TryRemoveDirectoryAt(parentHandle, rootName);
            parentHandle.Dispose();
            throw;
        }
        var transaction = new Transaction(rootName, destination, Environment.ProcessId,
            processStartUtcTicks, parentHandle, rootHandle);
        try
        {
            // The manifest is intentionally small and contains no file content or credentials.
            // Flush it before staging begins so a later Helper can identify an interrupted owner.
            var pendingManifest = transaction.PendingManifest;
            using (var manifest = new FileStream(pendingManifest, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                using (var writer = new StreamWriter(manifest, new System.Text.UTF8Encoding(false), 4096,
                           leaveOpen: true))
                {
                    writer.WriteLine("1");
                    writer.WriteLine(transaction.ProcessId.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine(transaction.ProcessStartUtcTicks.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(destination)));
                    writer.Flush();
                }
                manifest.Flush(flushToDisk: true);
            }
            RenameAt(transaction.RootHandle, PendingManifestName, transaction.RootHandle,
                ManifestName);
            return transaction;
        }
        catch
        {
            FinishTransaction(transaction);
            transaction.Dispose();
            throw;
        }
    }

    private static bool TryReadTransaction(string root, out Transaction transaction)
    {
        transaction = null!;
        SafeFileHandle? parentHandle = null;
        SafeFileHandle? rootHandle = null;
        try
        {
            var attributes = File.GetAttributes(root);
            if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;
            var parent = Path.GetDirectoryName(root)!;
            var rootName = Path.GetFileName(root);
            parentHandle = OpenDirectoryHandle(parent);
            rootHandle = OpenDirectoryHandleAt(parentHandle, rootName);
            var manifestPath = Path.Combine(DescriptorPath(rootHandle), ManifestName);
            if (new FileInfo(manifestPath).Length > 4096) return false;
            var lines = File.ReadAllLines(manifestPath);
            if (lines.Length != 4 || lines[0] != "1"
                || !int.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
                || processId <= 0
                || !long.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out var processStartUtcTicks)
                || processStartUtcTicks <= 0)
                return false;
            if (!TryReadOwnerFromTransactionName(root, out var namedProcessId,
                    out var namedProcessStartUtcTicks)
                || namedProcessId != processId || namedProcessStartUtcTicks != processStartUtcTicks)
                return false;
            var destination = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(lines[3]));
            if (!Path.IsPathFullyQualified(destination)) return false;
            destination = Path.GetFullPath(destination);
            if (!SamePath(Path.GetDirectoryName(destination)!, Path.GetDirectoryName(root)!)) return false;
            transaction = new(rootName, destination, processId, processStartUtcTicks,
                parentHandle, rootHandle);
            parentHandle = null;
            rootHandle = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or FormatException or ArgumentException)
        {
            return false;
        }
        finally
        {
            rootHandle?.Dispose();
            parentHandle?.Dispose();
        }
    }

    private static bool IsCreatingProcessAlive(Transaction transaction)
        => IsCreatingProcessAlive(transaction.ProcessId, transaction.ProcessStartUtcTicks);

    private static bool IsCreatingProcessAlive(int processId, long processStartUtcTicks)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == processStartUtcTicks;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static void RecoverIncompleteInitialization(string root)
    {
        SafeFileHandle? parentHandle = null;
        SafeFileHandle? rootHandle = null;
        try
        {
            var attributes = File.GetAttributes(root);
            if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint)
                || !TryReadOwnerFromTransactionName(root, out var processId, out var processStartUtcTicks)
                || IsCreatingProcessAlive(processId, processStartUtcTicks))
                return;
            var parent = Path.GetDirectoryName(root)!;
            var rootName = Path.GetFileName(root);
            parentHandle = OpenDirectoryHandle(parent);
            rootHandle = OpenDirectoryHandleAt(parentHandle, rootName);
            var anchoredRoot = DescriptorPath(rootHandle);
            var entries = Directory.EnumerateFileSystemEntries(anchoredRoot).ToArray();
            if (entries.Any(entry => Path.GetFileName(entry) is not (ManifestName or PendingManifestName)
                || (File.GetAttributes(entry) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
                return;
            foreach (var entry in entries) File.Delete(entry);
            RemoveDirectoryAt(parentHandle, rootName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or System.ComponentModel.Win32Exception)
        {
            // An invalid or inaccessible lookalike is not trusted as an application transaction.
        }
        finally
        {
            rootHandle?.Dispose();
            parentHandle?.Dispose();
        }
    }

    private static bool TryReadOwnerFromTransactionName(string root, out int processId,
        out long processStartUtcTicks)
    {
        processId = 0;
        processStartUtcTicks = 0;
        var name = Path.GetFileName(root);
        if (!name.StartsWith(TransactionPrefix, StringComparison.Ordinal)
            || !name.EndsWith(TransactionSuffix, StringComparison.Ordinal))
            return false;
        var value = name[TransactionPrefix.Length..^TransactionSuffix.Length];
        var parts = value.Split('-', StringSplitOptions.None);
        return parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out processId)
            && processId > 0
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture,
                out processStartUtcTicks)
            && processStartUtcTicks > 0
            && Guid.TryParseExact(parts[2], "N", out _);
    }

    private static bool FinishTransaction(Transaction transaction)
    {
        try
        {
            if (Exists(transaction.Backup))
            {
                if (Exists(transaction.AnchoredDestination)) DeleteIfPresent(transaction.Backup);
                else RenameAt(transaction.RootHandle, BackupName, transaction.ParentHandle,
                    transaction.DestinationName);
            }
            DeleteIfPresent(transaction.Staged);
            if (File.Exists(transaction.Manifest)) File.Delete(transaction.Manifest);
            if (File.Exists(transaction.PendingManifest)) File.Delete(transaction.PendingManifest);
            if (Directory.Exists(transaction.AnchoredRootAtParent))
                RemoveDirectoryAt(transaction.ParentHandle, transaction.RootName);
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

    private static SafeFileHandle OpenDirectoryHandle(string path)
    {
        var descriptor = open(path, OpenReadOnly | OpenDirectory | OpenCloseOnExec);
        return descriptor < 0
            ? throw NativeIOException("Could not open the user-execution parent directory")
            : new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static SafeFileHandle OpenDirectoryHandleAt(SafeFileHandle parent, string name)
    {
        var descriptor = openat(Descriptor(parent), name,
            OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec);
        return descriptor < 0
            ? throw NativeIOException("Could not open the user-execution transaction directory")
            : new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static void RenameAt(SafeFileHandle oldDirectory, string oldName,
        SafeFileHandle newDirectory, string newName)
    {
        if (renameat(Descriptor(oldDirectory), oldName, Descriptor(newDirectory), newName) != 0)
            throw NativeIOException("Could not atomically rename a user-execution path");
    }

    private static void RemoveDirectoryAt(SafeFileHandle parent, string name)
    {
        if (unlinkat(Descriptor(parent), name, RemoveDirectory) != 0)
            throw NativeIOException("Could not remove the user-execution transaction directory");
    }

    private static void TryRemoveDirectoryAt(SafeFileHandle parent, string name)
    {
        try { RemoveDirectoryAt(parent, name); }
        catch { }
    }

    private static int Descriptor(SafeFileHandle handle) => handle.DangerousGetHandle().ToInt32();
    private static string DescriptorPath(SafeFileHandle handle) => $"/proc/self/fd/{Descriptor(handle)}";
    private static IOException NativeIOException(string message)
        => new($"{message} (errno {Marshal.GetLastPInvokeError()}).");

    private sealed class Transaction(string rootName, string destination, int processId,
        long processStartUtcTicks, SafeFileHandle parentHandle, SafeFileHandle rootHandle) : IDisposable
    {
        public string RootName { get; } = rootName;
        public string Destination { get; } = destination;
        public string DestinationName { get; } = Path.GetFileName(destination);
        public int ProcessId { get; } = processId;
        public long ProcessStartUtcTicks { get; } = processStartUtcTicks;
        public SafeFileHandle ParentHandle { get; } = parentHandle;
        public SafeFileHandle RootHandle { get; } = rootHandle;
        public string AnchoredRootAtParent => Path.Combine(DescriptorPath(ParentHandle), RootName);
        public string AnchoredDestination => Path.Combine(DescriptorPath(ParentHandle), DestinationName);
        public string Manifest => Path.Combine(DescriptorPath(RootHandle), ManifestName);
        public string PendingManifest => Path.Combine(DescriptorPath(RootHandle), PendingManifestName);
        public string Staged => Path.Combine(DescriptorPath(RootHandle), StagedName);
        public string Backup => Path.Combine(DescriptorPath(RootHandle), BackupName);
        public void Dispose()
        {
            RootHandle.Dispose();
            ParentHandle.Dispose();
        }
    }

    [DllImport("libc.so.6", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc.so.6", SetLastError = true)] private static extern int openat(int directory, string path, int flags);
    [DllImport("libc.so.6", SetLastError = true)] private static extern int mkdirat(int directory, string path, uint mode);
    [DllImport("libc.so.6", SetLastError = true)] private static extern int renameat(int oldDirectory, string oldPath, int newDirectory, string newPath);
    [DllImport("libc.so.6", SetLastError = true)] private static extern int unlinkat(int directory, string path, int flags);
}
