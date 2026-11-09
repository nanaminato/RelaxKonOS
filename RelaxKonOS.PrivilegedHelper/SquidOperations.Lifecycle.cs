using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

internal static partial class SquidOperations
{
    private const string LifecyclePendingPath = DirectoryPath + "/relaxkonos-lifecycle.pending";
    private const string LifecycleDirectory = DirectoryPath + "/relaxkonos-lifecycle";
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record LifecycleRecord([property: JsonRequired] Guid OperationId,
        [property: JsonRequired] SquidServiceAction Action, [property: JsonRequired] bool Completed);

    [SupportedOSPlatform("linux")]
    private static async Task<PrivilegedOperationResult> RunLifecycleAsync(Guid id, SquidServiceAction? action)
    {
        if (!ValidLifecycleAction(action)) return Failure("squid.action.invalid", PrivilegedProblemCode.InvalidRequest);
        var path = LifecyclePath(id);
        EnsurePlainProtectedPath(path);
        if (File.Exists(path)) return ObserveLifecycle(id, action);
        if (File.Exists(LifecyclePendingPath)) return UnknownPublication();
        if (action is not (SquidServiceAction.Stop or SquidServiceAction.Disable))
        {
            if (File.Exists(PendingPath)) return UnknownPublication();
            if (!await IsManagedAsync()) return Failure("squid.configuration.adoption_required");
        }

        System.IO.Directory.CreateDirectory(LifecycleDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        LinuxDirectorySync.Flush(DirectoryPath);
        var record = new LifecycleRecord(id, action!.Value, false);
        WritePublication(path, record, overwrite: false);
        await WriteNewAsync(LifecyclePendingPath, id.ToString("D"));
        LinuxDirectorySync.Flush(DirectoryPath);

        var result = await RunAsync(Systemctl, [action.Value.ToString().ToLowerInvariant(), "squid.service"], ServiceActionTimeout);
        if (!result.Success) return await WithFailureOutputAsync(result with { Error = "squid.recovery.required" });
        WritePublication(path, record with { Completed = true }, overwrite: true);
        File.Delete(LifecyclePendingPath);
        LinuxDirectorySync.Flush(DirectoryPath);
        return Json(new SquidLifecycleReceipt(id, action.Value));
    }

    [SupportedOSPlatform("linux")]
    private static PrivilegedOperationResult ObserveLifecycle(Guid id, SquidServiceAction? action)
    {
        if (!ValidLifecycleAction(action)) return Failure("squid.action.invalid", PrivilegedProblemCode.InvalidRequest);
        var path = LifecyclePath(id);
        EnsurePlainProtectedPath(path);
        if (!File.Exists(path)) return UnknownPublication();
        using var stream = File.OpenRead(path);
        if (stream.Length is 0 or > 4096) throw new IOException("Invalid Squid lifecycle record size.");
        var record = JsonSerializer.Deserialize<LifecycleRecord>(stream);
        if (record is null || record.OperationId != id || !ValidLifecycleAction(record.Action))
            throw new IOException("Invalid Squid lifecycle record.");
        if (record.Action != action) return Failure("squid.idempotency_conflict", PrivilegedProblemCode.Conflict);
        if (!record.Completed || File.Exists(LifecyclePendingPath)) return UnknownPublication();
        LinuxDirectorySync.Flush(DirectoryPath);
        return Json(new SquidLifecycleReceipt(id, record.Action));
    }

    private static string LifecyclePath(Guid id) => Path.Combine(LifecycleDirectory, id.ToString("N") + ".json");

    private static bool ValidLifecycleAction(SquidServiceAction? action) =>
        action is { } value && Enum.IsDefined(value) && value is not (SquidServiceAction.Install or SquidServiceAction.Uninstall);
}
