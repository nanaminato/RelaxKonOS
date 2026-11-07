using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Privileged;

public static partial class PrivilegedOperationExecutor
{
    private const string NginxReloadReceiptDirectory = "/var/lib/relaxkonos-nginx-reloads";
    private sealed record NginxReloadRecord(Guid OperationId, bool Completed);

    private static async Task<PrivilegedOperationResult> RunTrackedNginxReloadAsync(Guid? operationId)
    {
        if (!OperatingSystem.IsLinux()) return Fail(69, PrivilegedProblemCode.UnsupportedOperation, "Linux is required");
        if (operationId is not Guid id || id == Guid.Empty) return Fail(65, PrivilegedProblemCode.InvalidRequest, "A reload operation ID is required");
        var path = ReloadReceiptPath(id);
        try
        {
            RuntimeSourceStore.RequireNoLinks(NginxReloadReceiptDirectory);
            Directory.CreateDirectory(NginxReloadReceiptDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            RequireProtectedReloadPath(NginxReloadReceiptDirectory);
            try { WriteReloadRecord(path, new(id, false), overwrite: false); }
            catch (IOException) when (File.Exists(path))
            {
                // An accepted ID is read-only, even after Helper restart.
                return QueryNginxReloadReceipt(id);
            }

            var result = await ApplyNginxSystemServiceActionAsync(NginxSystemServiceAction.Reload);
            // A local timeout or failed systemctl connection cannot establish that the systemd job ended.
            if (!result.Success) return UnknownReload();
            WriteReloadRecord(path, new(id, true), overwrite: true);
            return new(true, NginxReloadReceipt: new(id));
        }
        catch (Exception error) when (ReloadRecordError(error))
        {
            return UnknownReload();
        }
    }

    private static PrivilegedOperationResult QueryNginxReloadReceipt(Guid? operationId)
    {
        if (!OperatingSystem.IsLinux()) return Fail(69, PrivilegedProblemCode.UnsupportedOperation, "Linux is required");
        if (operationId is not Guid id || id == Guid.Empty) return Fail(65, PrivilegedProblemCode.InvalidRequest, "A reload operation ID is required");
        try
        {
            var path = ReloadReceiptPath(id);
            RequireProtectedReloadPath(NginxReloadReceiptDirectory);
            RequireProtectedReloadPath(path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length is <= 0 or > 512) return UnknownReload();
            var receipt = JsonSerializer.Deserialize<NginxReloadRecord>(input);
            return receipt is { Completed: true } && receipt.OperationId == id
                ? new(true, NginxReloadReceipt: new(id))
                : UnknownReload();
        }
        catch (Exception error) when (ReloadRecordError(error))
        {
            return UnknownReload();
        }
    }

    private static string ReloadReceiptPath(Guid id) =>
        Path.Combine(NginxReloadReceiptDirectory, id.ToString("N") + ".json");

    private static bool ReloadRecordError(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException or RuntimePolicyException;

    private static void RequireProtectedReloadPath(string path)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        var stat = new LinuxUnixSocketFileSystem().Stat(path) ?? throw new FileNotFoundException();
        if (stat.OwnerId != 0 || stat.IsSocket || (stat.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new UnauthorizedAccessException("The Nginx reload receipt path must be protected.");
    }

    private static void WriteReloadRecord(string path, NginxReloadRecord record, bool overwrite)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };
            using (var output = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(output, record);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally { File.Delete(temporary); }
    }

    private static PrivilegedOperationResult UnknownReload() =>
        Fail(69, PrivilegedProblemCode.RecoveryRequired, "The original Nginx reload result is unverified");
}
