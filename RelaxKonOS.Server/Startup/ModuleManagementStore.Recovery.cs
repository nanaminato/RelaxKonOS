using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Identity;

namespace RelaxKonOS.Server.Startup;

internal sealed partial class ModuleManagementStore
{
    internal void RecordStartupFailure(string stage)
    {
        if (stage is not ("selection" or "configuration" or "build" or "deactivation" or "storage" or "mapping" or "host"))
            throw new ArgumentOutOfRangeException(nameof(stage));
        lock (_gate)
        {
            ReadCurrent();
            var operations = _document.Operations.Select(operation => operation.DesiredRevision == _document.Revision
                && operation.State is ServerModuleOperationState.AwaitingRestart or ServerModuleOperationState.Verifying
                ? operation with { State = ServerModuleOperationState.RecoveryRequired, UpdatedAt = DateTimeOffset.UtcNow,
                    ProblemCode = "module-startup-" + stage + "-failed" } : operation).ToList();
            if (!operations.SequenceEqual(_document.Operations)) Commit(_document with { Operations = operations });
        }
    }

    private static bool RequiresStartupVerification(ServerModuleOperationDto operation) =>
        operation.State == ServerModuleOperationState.AwaitingRestart || operation.State == ServerModuleOperationState.RecoveryRequired
            && operation.ProblemCode is "module-startup-selection-failed" or "module-startup-configuration-failed"
                or "module-startup-build-failed" or "module-startup-deactivation-failed" or "module-startup-storage-failed"
                or "module-startup-mapping-failed" or "module-startup-host-failed";

    internal static void PreserveUnixOwner(string temporary, string reference)
    {
        var start = new System.Diagnostics.ProcessStartInfo("/bin/chown")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("--reference=" + reference);
        start.ArgumentList.Add("--"); start.ArgumentList.Add(temporary);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Module state ownership could not be preserved.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            throw new IOException("Module state ownership could not be preserved.");
        }
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new IOException("Module state ownership could not be preserved.");
    }
    internal static ServerModuleRecoveryReportDto ReadRecoveryReport(string contentRoot)
    {
        var document = ReadDocument(Path.Combine(contentRoot, "data", "module-management", "state.json"));
        return new(document.ServiceId, document.Revision, document.EnabledIds,
            document.Policies, document.Operations.Where(operation => operation.State is ServerModuleOperationState.Prepared
                or ServerModuleOperationState.Applying or ServerModuleOperationState.AwaitingRestart
                or ServerModuleOperationState.Verifying or ServerModuleOperationState.RecoveryRequired).ToArray(),
            DateTimeOffset.UtcNow, "desired-only");
    }

    internal static ModuleManagementStore OpenForLocalRecovery(IConfiguration configuration, string contentRoot, string serviceId, string revision)
    {
        if (!AuthMaintenanceCommand.IsAdministrator()) throw new ModuleManagementException("module-local-administrator-required", 403);
        if (configuration["ModuleManagement:Authority"] != "managed") throw new ModuleManagementException("module-configuration-read-only");
        // The service must already have initialized this store. Maintenance may run as a different
        // elevated identity, so it must preserve the installed service owner's directory ACL.
        if (!File.Exists(Path.Combine(contentRoot, "data", "module-management", "state.json")))
            throw new ModuleManagementException("module-state-not-found", 404);
        return new(configuration, contentRoot, false, serviceId, revision);
    }
}
