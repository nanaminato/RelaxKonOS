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
    private const int AtEmptyPath = 0x1000;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x7ff;
    private const uint RenameNoReplace = 1;
    private const ushort FileTypeMask = 0xf000;
    private const ushort DirectoryType = 0x4000;
    private const ushort SymbolicLinkType = 0xa000;
    private const ushort PermissionMask = 0x0fff;
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

    public static bool Copy(string source, string destination, bool overwrite)
    {
        source = NormalizePath(source);
        destination = NormalizePath(destination);
        if (SamePath(source, destination))
        {
            using var sameReference = OpenPathReference(source);
            return IsDirectory(sameReference.Stat);
        }
        using var sourceReference = OpenPathReference(source);
        var directory = IsDirectory(sourceReference.Stat);
        if (directory && ContainsPath(source, destination))
            throw new ArgumentException("A directory cannot be copied into its own descendant.");
        if (Exists(destination) && !overwrite) throw new IOException("Destination already exists.");
        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            throw new DirectoryNotFoundException("Destination directory does not exist.");

        using var transaction = BeginTransaction(destination);
        try
        {
            CopyEntry(sourceReference.ParentHandle, sourceReference.Name, sourceReference.Stat,
                transaction.Staged);
            Commit(transaction, overwrite);
            return directory;
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

    public static bool Move(string source, string destination, bool overwrite)
    {
        source = NormalizePath(source);
        destination = NormalizePath(destination);
        using var sourceReference = OpenPathReference(source);
        var sourceIsDirectory = IsDirectory(sourceReference.Stat);
        if (SamePath(source, destination)) return sourceIsDirectory;
        if (sourceIsDirectory && ContainsPath(source, destination))
            throw new ArgumentException("A directory cannot be moved into its own descendant.");
        using var destinationParent = OpenParentDirectory(destination, out var destinationName);
        var destinationExists = TryStatAt(destinationParent, destinationName, out _);
        if (destinationExists && !overwrite) throw new IOException("Destination already exists.");

        // Directory.Move cannot replace an existing tree. Stage first so the old destination
        // remains intact until the replacement is complete.
        if (sourceIsDirectory && destinationExists)
        {
            var copiedIdentity = CopyReferenced(sourceReference, destination, overwrite: true);
            DeleteReferencedIfUnchanged(sourceReference, copiedIdentity);
            return true;
        }

        try
        {
            RenameAt(sourceReference.ParentHandle, sourceReference.Name, destinationParent,
                destinationName);
            return sourceIsDirectory;
        }
        catch (NativeFileIOException exception) when (exception.Errno == CrossDeviceLink)
        {
            var copiedIdentity = CopyReferenced(sourceReference, destination, overwrite);
            DeleteReferencedIfUnchanged(sourceReference, copiedIdentity);
            return sourceIsDirectory;
        }
    }

    public static bool Delete(string path)
    {
        using var reference = OpenPathReference(path);
        DeleteEntryAt(reference.ParentHandle, reference.Name, reference.Stat);
        return true;
    }

    public static bool Rename(string path, string newName)
    {
        using var reference = OpenPathReference(path);
        if (TryStatAt(reference.ParentHandle, newName, out _))
            throw new IOException("Destination already exists.");
        RenameAtNoReplace(reference.ParentHandle, reference.Name, reference.ParentHandle, newName);
        return IsDirectory(reference.Stat);
    }

    internal static T WithAnchoredDirectory<T>(string path, Func<string, T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var handle = OpenDirectoryHandle(NormalizePath(path));
        return action(DescriptorPath(handle));
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

    private static NodeIdentity CopyReferenced(PathReference source, string destination,
        bool overwrite)
    {
        using var transaction = BeginTransaction(destination);
        try
        {
            var identity = CopyEntry(source.ParentHandle, source.Name, source.Stat,
                transaction.Staged);
            Commit(transaction, overwrite);
            return identity;
        }
        finally
        {
            FinishTransaction(transaction);
        }
    }

    private static NodeIdentity CopyEntry(SafeFileHandle sourceParent, string sourceName,
        StatxBuffer sourceStat, string destination)
    {
        if (IsDirectory(sourceStat))
        {
            using var source = OpenDirectoryHandleAt(sourceParent, sourceName);
            var openedStat = StatHandle(source);
            Directory.CreateDirectory(destination);
            foreach (var entry in Directory.EnumerateFileSystemEntries(DescriptorPath(source)))
            {
                var name = Path.GetFileName(entry);
                var stat = StatAt(source, name);
                CopyEntry(source, name, stat, Path.Combine(destination, name));
            }
            return Identity(openedStat);
        }

        if (IsSymbolicLink(sourceStat))
        {
            var target = ReadLinkAt(sourceParent, sourceName);
            File.CreateSymbolicLink(destination, target);
            return Identity(sourceStat);
        }

        using var sourceFile = OpenFileHandleAt(sourceParent, sourceName);
        var openedFileStat = StatHandle(sourceFile);
        using (var input = new FileStream(sourceFile, FileAccess.Read))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                   FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            input.CopyTo(output);
        File.SetUnixFileMode(destination, (UnixFileMode)(openedFileStat.Mode & PermissionMask));
        return Identity(openedFileStat);
    }

    private static void DeleteReferencedIfUnchanged(PathReference source,
        NodeIdentity copiedIdentity)
    {
        if (!TryStatAt(source.ParentHandle, source.Name, out var current)
            || Identity(current) != copiedIdentity)
            throw new IOException("Source changed while the move was in progress.");
        DeleteEntryAt(source.ParentHandle, source.Name, current);
    }

    private static void DeleteEntryAt(SafeFileHandle parent, string name, StatxBuffer stat)
    {
        if (!IsDirectory(stat))
        {
            UnlinkAt(parent, name, removeDirectory: false);
            return;
        }

        using var directory = OpenDirectoryHandleAt(parent, name);
        foreach (var entry in Directory.EnumerateFileSystemEntries(DescriptorPath(directory)))
        {
            var childName = Path.GetFileName(entry);
            DeleteEntryAt(directory, childName, StatAt(directory, childName));
        }
        UnlinkAt(parent, name, removeDirectory: true);
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
    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Path.GetPathRoot(fullPath) == fullPath
            ? fullPath
            : Path.TrimEndingDirectorySeparator(fullPath);
    }
    private static bool SamePath(string left, string right) => string.Equals(
        NormalizePath(left), NormalizePath(right), StringComparison.Ordinal);
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

    private static SafeFileHandle OpenFileHandleAt(SafeFileHandle parent, string name)
    {
        var descriptor = openat(Descriptor(parent), name,
            OpenReadOnly | OpenNoFollow | OpenCloseOnExec);
        return descriptor < 0
            ? throw NativeIOException("Could not open the user-execution source file")
            : new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static SafeFileHandle OpenParentDirectory(string path, out string name)
    {
        path = NormalizePath(path);
        var parent = Path.GetDirectoryName(path);
        name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
            throw new ArgumentException("A filesystem root cannot be used for this operation.", nameof(path));
        return OpenDirectoryHandle(parent);
    }

    private static PathReference OpenPathReference(string path)
    {
        var handle = OpenParentDirectory(path, out var name);
        try { return new PathReference(handle, name, StatAt(handle, name)); }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void RenameAt(SafeFileHandle oldDirectory, string oldName,
        SafeFileHandle newDirectory, string newName)
    {
        if (renameat(Descriptor(oldDirectory), oldName, Descriptor(newDirectory), newName) != 0)
            throw NativeIOException("Could not atomically rename a user-execution path");
    }

    private static void RenameAtNoReplace(SafeFileHandle oldDirectory, string oldName,
        SafeFileHandle newDirectory, string newName)
    {
        if (renameat2(Descriptor(oldDirectory), oldName, Descriptor(newDirectory), newName,
                RenameNoReplace) != 0)
            throw NativeIOException("Could not atomically rename a user-execution path");
    }

    private static void UnlinkAt(SafeFileHandle parent, string name, bool removeDirectory)
    {
        if (unlinkat(Descriptor(parent), name, removeDirectory ? RemoveDirectory : 0) != 0)
            throw NativeIOException("Could not delete a user-execution path");
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
    private static StatxBuffer StatAt(SafeFileHandle parent, string name)
        => TryStatAt(parent, name, out var stat)
            ? stat
            : throw NativeIOException("Could not inspect a user-execution path");

    private static bool TryStatAt(SafeFileHandle parent, string name, out StatxBuffer stat)
    {
        if (statx(Descriptor(parent), name, AtSymlinkNoFollow, StatxBasicStats, out stat) == 0)
            return true;
        var error = Marshal.GetLastPInvokeError();
        if (error is 2 or 20) return false;
        throw new NativeFileIOException("Could not inspect a user-execution path", error);
    }

    private static StatxBuffer StatHandle(SafeFileHandle handle)
    {
        if (statx(Descriptor(handle), string.Empty, AtEmptyPath, StatxBasicStats, out var stat) != 0)
            throw NativeIOException("Could not inspect an opened user-execution path");
        return stat;
    }

    private static bool IsDirectory(StatxBuffer stat)
        => (stat.Mode & FileTypeMask) == DirectoryType;
    private static bool IsSymbolicLink(StatxBuffer stat)
        => (stat.Mode & FileTypeMask) == SymbolicLinkType;
    private static NodeIdentity Identity(StatxBuffer stat)
        => new(stat.DeviceMajor, stat.DeviceMinor, stat.Inode);

    private static string ReadLinkAt(SafeFileHandle parent, string name)
    {
        var buffer = new byte[4096];
        var length = readlinkat(Descriptor(parent), name, buffer, (nuint)buffer.Length);
        if (length < 0) throw NativeIOException("Could not read a user-execution symbolic link");
        if (length == buffer.Length) throw new IOException("Symbolic link target is too long.");
        return System.Text.Encoding.UTF8.GetString(buffer, 0, checked((int)length));
    }

    private static NativeFileIOException NativeIOException(string message)
        => new(message, Marshal.GetLastPInvokeError());

    private sealed class NativeFileIOException(string message, int errno)
        : IOException($"{message} (errno {errno}).")
    {
        public int Errno { get; } = errno;
    }

    private sealed class PathReference(SafeFileHandle parentHandle, string name, StatxBuffer stat)
        : IDisposable
    {
        public SafeFileHandle ParentHandle { get; } = parentHandle;
        public string Name { get; } = name;
        public StatxBuffer Stat { get; } = stat;
        public void Dispose() => ParentHandle.Dispose();
    }

    private readonly record struct NodeIdentity(uint DeviceMajor, uint DeviceMinor, ulong Inode);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

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
    [DllImport("libc.so.6", SetLastError = true)] private static extern int renameat2(int oldDirectory, string oldPath, int newDirectory, string newPath, uint flags);
    [DllImport("libc.so.6", SetLastError = true)] private static extern int unlinkat(int directory, string path, int flags);
    [DllImport("libc.so.6", SetLastError = true)] private static extern long readlinkat(int directory, string path, byte[] buffer, nuint bufferSize);
    [DllImport("libc.so.6", SetLastError = true)] private static extern int statx(int directory, string path, int flags, uint mask, out StatxBuffer stat);
}
