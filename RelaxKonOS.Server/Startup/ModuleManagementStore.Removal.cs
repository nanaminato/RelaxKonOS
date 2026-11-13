using System.Text.Json;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Server.Startup;

internal sealed partial class ModuleManagementStore
{
    // Called under the management gate and the HTTP package mutation gate. Removal only accepts
    // modules that are disabled in both the desired configuration and this process's startup graph.
    private ServerModuleOperationDto ApplyRemoval(SavedPlan saved)
    {
        var target = saved.RemovalTarget ?? throw new ModuleManagementException("module-package-installed-module-not-found");
        var installed = InstalledModuleStore.Read(_contentRoot);
        var current = installed.SingleOrDefault(item => item.ModuleId == target.ModuleId);
        // Compare the frozen installation fact, not merely a version string. A package update does
        // not advance the management revision, so revision validation alone is insufficient.
        if (JsonSerializer.Serialize(current, RelaxKonOSJsonOptions.Default)
            != JsonSerializer.Serialize(target, RelaxKonOSJsonOptions.Default))
            throw new ModuleManagementException("module-package-plan-target-changed");
        var operationId = Guid.NewGuid();
        var source = Path.Combine(_contentRoot, "data", "module-management", "packages", target.ModuleId);
        var archive = Path.Combine(_contentRoot, "data", "module-management", "removal-backups", operationId.ToString("N"));
        // Never follow a junction or symbolic link while moving the package tree. Module data is
        // outside this tree and is intentionally neither visited nor removed.
        CheckRemovalPath(source);
        CheckRemovalPath(Path.GetDirectoryName(archive)!);
        var now = DateTimeOffset.UtcNow;
        var policies = new Dictionary<string, ServerModuleResidencyPolicyDto>(_document.Policies, StringComparer.Ordinal);
        policies.Remove(target.ModuleId);
        var operation = new ServerModuleOperationDto(operationId, saved.Plan.PlanId, ServiceId,
            ServerModuleOperationState.Prepared, now, now, Guid.NewGuid().ToString("N"), _document.EnabledIds);
        Commit(_document with
        {
            Revision = operation.DesiredRevision, Policies = policies,
            Plans = _document.Plans.Select(item => item == saved ? item with { OperationId = operationId } : item).ToList(),
            Operations = [.. _document.Operations, operation]
        });
        try
        {
            Directory.CreateDirectory(archive);
            File.WriteAllBytes(Path.Combine(archive, "installed.json"),
                JsonSerializer.SerializeToUtf8Bytes(target, RelaxKonOSJsonOptions.Default));
            // Unregister first: after interruption, startup never tries to execute a missing package.
            // The original installation fact and all package bytes remain available for recovery.
            InstalledModuleStore.Write(_contentRoot, installed.Where(item => item.ModuleId != target.ModuleId).ToArray());
            if (Directory.Exists(source)) Directory.Move(source, Path.Combine(archive, "packages"));
            operation = operation with { State = ServerModuleOperationState.Succeeded,
                UpdatedAt = DateTimeOffset.UtcNow,
                ModuleResults = [new(target.ModuleId, true)] };
            Commit(_document with { Revision = operation.DesiredRevision, Policies = policies,
                Operations = _document.Operations.Select(item => item.OperationId == operationId ? operation : item).ToList() });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ModulePackageException)
        {
            // A partial filesystem operation is never reported as success or blindly replayed.
            // Retain the backup and an explicit receipt for operator recovery.
            operation = operation with { State = ServerModuleOperationState.RecoveryRequired,
                UpdatedAt = DateTimeOffset.UtcNow, ProblemCode = "module-remove-recovery-required" };
            Commit(_document with { Operations = _document.Operations.Select(item => item.OperationId == operationId ? operation : item).ToList() });
        }
        return operation;
    }

    private void CheckRemovalPath(string path)
    {
        var root = Path.GetFullPath(_contentRoot);
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ModuleManagementException("module-package-installed-path-invalid");
            if (current == root) return;
        }
        throw new ModuleManagementException("module-package-installed-path-invalid");
    }
}
