using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Privileged;

/// <summary>
/// Module data-lifecycle adapter. It names one exact Host-approved data root, freezes a structural
/// digest of every regular file, and only then preserves, backs up or deletes it. No arbitrary path,
/// recursive purge of a parent tree, symbolic-link escape or script is accepted.
/// </summary>
public static partial class PrivilegedOperationExecutor
{
    private const string ModuleDataBackupRoot = ModuleResourceStateRoot + "/backups";
    private const string ModuleDataPreservedRoot = ModuleResourceStateRoot + "/preserved";
    private const long ModuleDataMaximumFiles = 100000;

    private sealed record ModuleDataPreservation(string Root, long Files, long Bytes, string Digest,
        DateTimeOffset PreservedAt);

    private sealed record ModuleDataBackup(string Root, long Files, long Bytes, string Digest, string BackupRef,
        DateTimeOffset CreatedAt);

    private sealed class ModuleDataInvalidException : Exception
    {
    }

    private sealed class ModuleDataTooLargeException : Exception
    {
    }

    private static Task<PrivilegedOperationResult> QueryModuleDataAsync(ModuleDataQuery? query)
    {
        if (!OperatingSystem.IsLinux())
            return Task.FromResult(Fail(69, PrivilegedProblemCode.UnsupportedOperation, "module data host is unsupported"));
        if (query is null || !ModuleResourceIdentity.CanonicalPath(query.Root) || !Enum.IsDefined(query.Action))
            return Task.FromResult(Fail(64, PrivilegedProblemCode.InvalidRequest, "module data query is invalid"));
        if (!ModuleResourcePolicy.TryLoad(ModuleResourcePolicyPath, out var policy, out _) || policy is null)
            return Task.FromResult(Fail(64, PrivilegedProblemCode.UnsupportedOperation, "module resource policy unavailable"));
        if (!policy.TryFindData(query.Root, ModuleDataActionName(query.Action), out var entry) || entry is null)
            return Task.FromResult(Fail(64, PrivilegedProblemCode.AccessDenied, "module data root is not approved"));
        try
        {
            var state = ReadModuleDataState(entry, entry.Root);
            return Task.FromResult(new PrivilegedOperationResult(true, ModuleData: state));
        }
        catch (ModuleDataTooLargeException)
        {
            return Task.FromResult(Fail(65, PrivilegedProblemCode.ContentTooLarge, "module data root exceeds its approved size"));
        }
        catch (Exception error) when (error is ModuleDataInvalidException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Fail(77, PrivilegedProblemCode.ResourceNotAllowed, "module data root is not a plain directory tree"));
        }
    }

    private static Task<PrivilegedOperationResult> ApplyModuleDataTransactionAsync(ModuleDataTransaction? transaction,
        Guid operationId)
    {
        if (!OperatingSystem.IsLinux())
            return Task.FromResult(Fail(69, PrivilegedProblemCode.UnsupportedOperation, "module data host is unsupported"));
        if (transaction is null || !Enum.IsDefined(transaction.Action)
            || !ModuleResourceIdentity.CanonicalPath(transaction.Root)
            || !ModuleResourceIdentity.Identifier(transaction.Scope)
            || !ModuleResourceIdentity.Usable(transaction.Expected)
            || transaction.Expected is { Exists: false })
            return Task.FromResult(Fail(64, PrivilegedProblemCode.InvalidRequest, "module data transaction is invalid"));
        if (transaction.Action == ModuleDataAction.Backup
            ? !ModuleResourceIdentity.Identifier(transaction.BackupRef)
            : transaction.BackupRef is not null)
            return Task.FromResult(Fail(64, PrivilegedProblemCode.InvalidRequest, "module data backup reference is invalid"));
        if (!ModuleResourcePolicy.TryLoad(ModuleResourcePolicyPath, out var policy, out _) || policy is null)
            return Task.FromResult(Fail(64, PrivilegedProblemCode.UnsupportedOperation, "module resource policy unavailable"));
        if (!policy.TryFindData(transaction.Root, ModuleDataActionName(transaction.Action), out var entry) || entry is null)
            return Task.FromResult(Fail(65, PrivilegedProblemCode.PlanChanged, "module data root is no longer approved"));
        if (transaction.Expected.Bytes > entry.MaximumBytes || transaction.Expected.Files > ModuleDataMaximumFiles)
            return Task.FromResult(Fail(65, PrivilegedProblemCode.ContentTooLarge, "module data root exceeds its approved size"));
        var receiptPath = Path.Combine(ModuleResourceStateRoot, "operations", operationId.ToString("N") + ".json");
        if (File.Exists(receiptPath))
            return Task.FromResult(Fail(65, PrivilegedProblemCode.Conflict, "module resource operation requires recovery"));
        try
        {
            var current = ReadModuleDataState(entry, entry.Root);
            if (!SameDataState(current, transaction.Expected))
                return Task.FromResult(Fail(65, PrivilegedProblemCode.PlanChanged, "module data root changed"));
            var receipt = new ModuleResourceReceipt(operationId, "data", ModuleDataActionName(transaction.Action),
                transaction.Root, transaction.BackupRef ?? "", transaction.Expected.Digest,
                transaction.BackupRef ?? transaction.Expected.Digest, false);
            WriteModuleResourceReceipt(receiptPath, receipt);
            switch (transaction.Action)
            {
                case ModuleDataAction.Preserve:
                    WriteModuleResourceJson(Path.Combine(ModuleDataPreservedRoot, transaction.Scope,
                            transaction.Expected.Digest + ".json"),
                        new ModuleDataPreservation(transaction.Root, transaction.Expected.Files,
                            transaction.Expected.Bytes, transaction.Expected.Digest, DateTimeOffset.UtcNow));
                    break;
                case ModuleDataAction.Backup:
                    BackupModuleData(entry, transaction);
                    break;
                case ModuleDataAction.Delete:
                    DeleteModuleData(entry);
                    break;
            }
            WriteModuleResourceReceipt(receiptPath, receipt with { Committed = true });
            return Task.FromResult(new PrivilegedOperationResult(true));
        }
        catch (ModuleDataTooLargeException)
        {
            return Task.FromResult(Fail(65, PrivilegedProblemCode.ContentTooLarge, "module data root exceeds its approved size"));
        }
        catch (ModuleDataInvalidException)
        {
            return Task.FromResult(Fail(77, PrivilegedProblemCode.ResourceNotAllowed, "module data root is not a plain directory tree"));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(Fail(77, PrivilegedProblemCode.AccessDenied, "module data access denied"));
        }
        catch (Exception error) when (error is IOException or ArgumentException)
        {
            return Task.FromResult(Fail(65, PrivilegedProblemCode.Conflict, "module data transaction failed"));
        }
    }

    private static void BackupModuleData(ModuleResourceRoot entry, ModuleDataTransaction transaction)
    {
        var destination = Path.Combine(ModuleDataBackupRoot, transaction.Scope, transaction.BackupRef!);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("The module data backup already exists.");
        Directory.CreateDirectory(ModuleDataBackupRoot,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(ModuleDataBackupRoot,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // The declared root itself is the source. Anchoring its parent (and the backup root) keeps the
        // source directory descriptor pinned without making the root's own parent a mutable target.
        using var anchors = LinuxUserFileOperations.AnchorAllowedRoots(
            [Path.GetDirectoryName(entry.Root)!, ModuleDataBackupRoot]);
        try
        {
            LinuxUserFileOperations.Copy(entry.Root, destination, overwrite: false);
            var copy = ReadModuleDataState(entry, destination);
            if (!SameDataState(copy, transaction.Expected))
                throw new ModuleDataInvalidException();
            WriteModuleResourceJson(Path.Combine(destination, "manifest.json"),
                new ModuleDataBackup(transaction.Root, transaction.Expected.Files, transaction.Expected.Bytes,
                    transaction.Expected.Digest, transaction.BackupRef!, DateTimeOffset.UtcNow));
        }
        catch
        {
            try { if (Directory.Exists(destination)) LinuxUserFileOperations.Delete(destination); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or ArgumentException) { }
            throw;
        }
    }

    private static void DeleteModuleData(ModuleResourceRoot entry)
    {
        using var anchors = LinuxUserFileOperations.AnchorAllowedRoots([entry.Root]);
        foreach (var child in Directory.EnumerateFileSystemEntries(entry.Root))
            LinuxUserFileOperations.Delete(child);
        if (Directory.EnumerateFileSystemEntries(entry.Root).Any())
            throw new IOException("The module data root could not be emptied.");
    }

    private static ModuleDataState ReadModuleDataState(ModuleResourceRoot entry, string root)
    {
        var metadata = LinuxUserFileOperations.GetMetadata(root);
        if (metadata is null) return new(false, false, 0, 0, "");
        if (!metadata.IsDirectory || metadata.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || metadata.Attributes.HasFlag(FileAttributes.Device))
            return new(true, false, 0, 0, "");
        var entries = new List<(string Relative, long Bytes, string Sha256)>();
        long total = 0;
        void Walk(string directory, string prefix)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                var name = Path.GetFileName(path);
                var relative = prefix.Length == 0 ? name : prefix + "/" + name;
                if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Device))
                    throw new ModuleDataInvalidException();
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    Walk(path, relative);
                    continue;
                }
                var bytes = new FileInfo(path).Length;
                total += bytes;
                if (total > entry.MaximumBytes || entries.Count >= ModuleDataMaximumFiles)
                    throw new ModuleDataTooLargeException();
                using var stream = LinuxUserFileOperations.OpenRead(path);
                var sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
                entries.Add((relative, bytes, sha256));
            }
        }
        Walk(root, "");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        foreach (var item in entries.OrderBy(item => item.Relative, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(item.Relative));
            hash.AppendData([0]);
            BinaryPrimitives.WriteInt64LittleEndian(length, item.Bytes);
            hash.AppendData(length);
            hash.AppendData(Convert.FromHexString(item.Sha256));
        }
        return new(true, true, entries.Count, total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static bool SameDataState(ModuleDataState left, ModuleDataState right) =>
        left.Exists == right.Exists && left.IsDirectory == right.IsDirectory
        && left.Files == right.Files && left.Bytes == right.Bytes && left.Digest == right.Digest;
}
