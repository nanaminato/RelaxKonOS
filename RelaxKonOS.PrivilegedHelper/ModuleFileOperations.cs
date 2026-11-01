using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Privileged;

/// <summary>
/// Module configuration-file adapter. It never accepts a path outside the Host-approved root, an owner
/// name, a mode expression or an executable. Content is published beside the destination and renamed,
/// so a failed write never exposes a partial configuration file.
/// </summary>
public static partial class PrivilegedOperationExecutor
{
    private const string ModuleResourcePolicyPath = "/etc/relaxkonos/module-resources.json";
    private const string ModuleResourceStateRoot = "/var/lib/relaxkonos/module-resources";
    private const int ModuleFileMaximumContentBytes = 8 * 1024 * 1024;

    private sealed record ModuleResourceReceipt(Guid OperationId, string Kind, string Action, string Root,
        string Target, string ExpectedDigest, string ResultDigest, bool Committed);

    private static string ModuleFileActionName(ModuleFileAction action) => action switch
    {
        ModuleFileAction.Write => "write",
        ModuleFileAction.Delete => "delete",
        _ => "",
    };

    private static string ModuleDataActionName(ModuleDataAction action) => action switch
    {
        ModuleDataAction.Preserve => "preserve",
        ModuleDataAction.Backup => "backup",
        ModuleDataAction.Delete => "delete",
        _ => "",
    };

    /// <summary>Read-only observation through the Helper's own root policy. It neither writes nor creates
    /// any directory, so a failed plan leaves no trace on the host.</summary>
    private static Task<PrivilegedOperationResult> QueryModuleFileAsync(ModuleFileQuery? query)
    {
        if (!OperatingSystem.IsLinux())
            return Task.FromResult(Fail(69, PrivilegedProblemCode.UnsupportedOperation, "module file host is unsupported"));
        if (query is null || !ModuleResourceIdentity.CanonicalPath(query.Root)
            || !ModuleResourceIdentity.WithinRoot(query.Root, query.Path) || !Enum.IsDefined(query.Action))
            return Task.FromResult(Fail(64, PrivilegedProblemCode.InvalidRequest, "module file query is invalid"));
        if (!ModuleResourcePolicy.TryLoad(ModuleResourcePolicyPath, out var policy, out _) || policy is null)
            return Task.FromResult(Fail(64, PrivilegedProblemCode.UnsupportedOperation, "module resource policy unavailable"));
        if (!policy.TryFindFile(query.Root, ModuleFileActionName(query.Action), out var entry) || entry is null)
            return Task.FromResult(Fail(64, PrivilegedProblemCode.AccessDenied, "module file root is not approved"));
        try
        {
            using var anchors = LinuxUserFileOperations.AnchorAllowedRoots([entry.Root]);
            var state = ReadModuleFileState(entry, query.Path);
            return Task.FromResult(state is null
                ? Fail(77, PrivilegedProblemCode.ResourceNotAllowed, "module file path is not a regular file")
                : new PrivilegedOperationResult(true, ModuleFile: state));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(Fail(77, PrivilegedProblemCode.AccessDenied, "module file access denied"));
        }
        catch (Exception error) when (error is IOException or ArgumentException)
        {
            return Task.FromResult(Fail(65, PrivilegedProblemCode.Conflict, "module file state is unavailable"));
        }
    }

    /// <summary>
    /// Applies one frozen configuration publication. A durable receipt is written before mutation, so a
    /// replay or interrupted attempt is reported as recovery-required and never re-applied.
    /// </summary>
    private static Task<PrivilegedOperationResult> ApplyModuleFileTransactionAsync(ModuleFileTransaction? transaction,
        Guid operationId)
    {
        if (!OperatingSystem.IsLinux())
            return Task.FromResult(Fail(69, PrivilegedProblemCode.UnsupportedOperation, "module file host is unsupported"));
        if (transaction is null || !Enum.IsDefined(transaction.Action)
            || !ModuleResourceIdentity.CanonicalPath(transaction.Root)
            || !ModuleResourceIdentity.WithinRoot(transaction.Root, transaction.Path)
            || !ModuleResourceIdentity.Usable(transaction.Expected))
            return Task.FromResult(Fail(64, PrivilegedProblemCode.InvalidRequest, "module file transaction is invalid"));
        if (!ModuleResourcePolicy.TryLoad(ModuleResourcePolicyPath, out var policy, out _) || policy is null)
            return Task.FromResult(Fail(64, PrivilegedProblemCode.UnsupportedOperation, "module resource policy unavailable"));
        if (!policy.TryFindFile(transaction.Root, ModuleFileActionName(transaction.Action), out var entry) || entry is null)
            return Task.FromResult(Fail(65, PrivilegedProblemCode.PlanChanged, "module file root is no longer approved"));
        var desiredMode = transaction.Expected.Exists ? transaction.Expected.Mode : entry.Mode;
        var desiredOwner = transaction.Expected.Exists ? transaction.Expected.Owner : entry.Owner;
        var desiredGroup = transaction.Expected.Exists ? transaction.Expected.Group : entry.Group;
        if (transaction.Action == ModuleFileAction.Write)
        {
            if (transaction.ContentBase64 is null || transaction.Mode != desiredMode
                || transaction.ContentBytes is < 0 or > ModuleFileMaximumContentBytes
                || entry.MaximumBytes < transaction.ContentBytes)
                return Task.FromResult(Fail(64, PrivilegedProblemCode.InvalidRequest, "module file content is invalid"));
        }
        else if (transaction.ContentBase64 is not null || transaction.ContentSha256 != "" || transaction.ContentBytes != 0)
        {
            return Task.FromResult(Fail(64, PrivilegedProblemCode.InvalidRequest, "a file delete cannot carry content"));
        }
        byte[]? content = null;
        if (transaction.Action == ModuleFileAction.Write)
        {
            try { content = Convert.FromBase64String(transaction.ContentBase64!); }
            catch (FormatException)
            { return Task.FromResult(Fail(64, PrivilegedProblemCode.InvalidRequest, "module file content is invalid")); }
            if (content.LongLength != transaction.ContentBytes
                || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)), transaction.ContentSha256, StringComparison.Ordinal))
                return Task.FromResult(Fail(64, PrivilegedProblemCode.InvalidRequest, "module file content digest is invalid"));
        }
        var receiptPath = Path.Combine(ModuleResourceStateRoot, "operations", operationId.ToString("N") + ".json");
        if (File.Exists(receiptPath))
            return Task.FromResult(Fail(65, PrivilegedProblemCode.Conflict, "module resource operation requires recovery"));
        try
        {
            using var anchors = LinuxUserFileOperations.AnchorAllowedRoots([entry.Root]);
            var current = ReadModuleFileState(entry, transaction.Path);
            if (current is null || !SameFileState(current, transaction.Expected))
                return Task.FromResult(Fail(65, PrivilegedProblemCode.PlanChanged, "module file state changed"));
            var receipt = new ModuleResourceReceipt(operationId, "file", ModuleFileActionName(transaction.Action),
                transaction.Root, transaction.Path, transaction.Expected.Sha256, transaction.ContentSha256, false);
            WriteModuleResourceReceipt(receiptPath, receipt);
            // Re-check immediately before mutation. The receipt write is the only work between the first
            // observation and this point; a file that moved in that window must not be overwritten.
            if (ReadModuleFileState(entry, transaction.Path) is not { } beforeMutation
                || !SameFileState(beforeMutation, transaction.Expected))
                return Task.FromResult(Fail(65, PrivilegedProblemCode.PlanChanged, "module file state changed"));
            if (transaction.Action == ModuleFileAction.Write)
            {
                LinuxUserFileOperations.WriteAllBytes(transaction.Path, content!);
                LinuxUserFileOperations.SetUnixFileMode(transaction.Path, (UnixFileMode)desiredMode, recursive: false);
                LinuxUserFileOperations.SetUnixOwnership(transaction.Path, desiredOwner.ToString(),
                    desiredGroup.ToString(), recursive: false);
                var final = ReadModuleFileState(entry, transaction.Path);
                if (final is null || final.Sha256 != transaction.ContentSha256
                    || final.Bytes != transaction.ContentBytes || final.Mode != desiredMode
                    || final.Owner != desiredOwner || final.Group != desiredGroup)
                    return Task.FromResult(Fail(65, PrivilegedProblemCode.InternalError,
                        "module file publication could not be verified"));
            }
            else
            {
                LinuxUserFileOperations.Delete(transaction.Path);
                if (ReadModuleFileState(entry, transaction.Path) is { Exists: true })
                    return Task.FromResult(Fail(65, PrivilegedProblemCode.InternalError, "module file deletion could not be verified"));
            }
            WriteModuleResourceReceipt(receiptPath, receipt with { Committed = true });
            return Task.FromResult(new PrivilegedOperationResult(true));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(Fail(77, PrivilegedProblemCode.AccessDenied, "module file access denied"));
        }
        catch (Exception error) when (error is IOException or ArgumentException)
        {
            return Task.FromResult(Fail(65, PrivilegedProblemCode.Conflict, "module file transaction failed"));
        }
    }

    private static ModuleFileState? ReadModuleFileState(ModuleResourceRoot entry, string path)
    {
        var metadata = LinuxUserFileOperations.GetMetadata(path);
        if (metadata is null)
            return new ModuleFileState(false, false, false, "", 0, 0, 0, entry.Mode);
        // Configuration files never carry setuid/setgid/sticky bits through this adapter. Reading
        // only the permission bits means a write cannot preserve a privileged mode on module content.
        var mode = (int)metadata.UnixMode & 0x1ff;
        // Reparse points and device/special files are never publishable configuration targets. They are
        // reported as non-regular so the Host refuses the plan instead of opening a FIFO or device.
        if (metadata.IsDirectory || metadata.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || metadata.Attributes.HasFlag(FileAttributes.Device))
            return new ModuleFileState(true, metadata.IsDirectory,
                metadata.Attributes.HasFlag(FileAttributes.ReparsePoint) || metadata.Attributes.HasFlag(FileAttributes.Device),
                "", checked((long)metadata.Size), metadata.Owner, metadata.Group, mode);
        using var stream = LinuxUserFileOperations.OpenRead(path);
        var digest = Convert.ToHexStringLower(SHA256.HashData(stream));
        return new ModuleFileState(true, false, false, digest, checked((long)metadata.Size), metadata.Owner,
            metadata.Group, mode);
    }

    private static bool SameFileState(ModuleFileState left, ModuleFileState right) =>
        left.Exists == right.Exists && left.IsDirectory == right.IsDirectory
        && left.IsSymbolicLink == right.IsSymbolicLink && left.Sha256 == right.Sha256
        && left.Bytes == right.Bytes && left.Owner == right.Owner && left.Group == right.Group
        && left.Mode == right.Mode;

    private static void WriteModuleResourceReceipt(string path, ModuleResourceReceipt receipt) =>
        WriteModuleResourceJson(path, receipt);

    /// <summary>Durable root-owned evidence. It contains no file content, only path, digest and counts.</summary>
    private static void WriteModuleResourceJson<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, value);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
