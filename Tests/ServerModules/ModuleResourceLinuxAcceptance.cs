extern alias modulehelper;

using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.Privileged;

/// <summary>
/// Root-only Linux acceptance for the DC-03 file/data adapters. It writes the documented root policy,
/// publishes a configuration file, freezes and backs up a data tree, deletes it, and proves that a
/// revoked root and a moved digest are refused. It never touches a root outside its own temporary tree.
/// </summary>
internal static class ModuleResourceLinuxAcceptance
{
    private const string PolicyPath = "/etc/relaxkonos/module-resources.json";
    private const string StateRoot = "/var/lib/relaxkonos/module-resources";
    private static readonly modulehelper::PrivilegedOperationPolicy HelperPolicy = new([], [], [], [], null);

    internal static async Task RunAsync(string[] args, Action<bool, string> check)
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName != "root")
            throw new InvalidOperationException("The module resource Linux acceptance must run as root on Linux.");
        var baseRoot = args.Length > 0 ? Path.GetFullPath(args[0])
            : Path.Combine("/var/tmp", "relaxkonos-dc03-" + Guid.NewGuid().ToString("N"));
        var fileRoot = Path.Combine(baseRoot, "etc");
        var dataRoot = Path.Combine(baseRoot, "data");
        var scope = "acceptance";
        var backupRef = "pre-delete";
        Directory.CreateDirectory(fileRoot);
        Directory.CreateDirectory(dataRoot);
        var originalPolicy = File.Exists(PolicyPath) ? await File.ReadAllBytesAsync(PolicyPath) : null;
        try
        {
            await WritePolicyAsync(fileRoot, dataRoot);
            var configPath = Path.Combine(fileRoot, "mysql.cnf");
            var content = "innodb_buffer_pool_size=1G\n";
            var contentSha = Sha(content);

            var absent = await QueryAsync(PrivilegedOperationKind.ModuleFileQuery,
                moduleFileQuery: new(fileRoot, configPath, ModuleFileAction.Write));
            var fileAbsent = absent.ModuleFile;
            check(absent.Success && fileAbsent is { Exists: false, Mode: 420 }
                && !fileAbsent.IsDirectory && !fileAbsent.IsSymbolicLink,
                "Linux Helper observes an absent configuration file through its root-owned policy");

            var fileOperation = Guid.NewGuid();
            var write = await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleFileApply,
                OperationId: fileOperation, Correlation: CorrelationContext.Create(fileOperation),
                ModuleFileApply: new(ModuleFileAction.Write, fileRoot, configPath, fileAbsent, contentSha,
                    Encoding.UTF8.GetByteCount(content), Convert.ToBase64String(Encoding.UTF8.GetBytes(content)), 420)));
            check(write.Success && File.Exists(configPath) && await File.ReadAllTextAsync(configPath) == content
                && (File.GetUnixFileMode(configPath) & (UnixFileMode)0x1ff) == (UnixFileMode)420,
                "Linux Helper publishes the exact confirmed bytes with the policy mode");

            var observed = await QueryAsync(PrivilegedOperationKind.ModuleFileQuery,
                moduleFileQuery: new(fileRoot, configPath, ModuleFileAction.Write));
            var observedFile = observed.ModuleFile;
            check(observed.Success && observedFile is { Exists: true, Sha256: var observedSha }
                && observedSha == contentSha && observedFile.Bytes == Encoding.UTF8.GetByteCount(content),
                "Linux Helper re-observes the published content digest");

            var driftOperation = Guid.NewGuid();
            var drift = await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleFileApply,
                OperationId: driftOperation, Correlation: CorrelationContext.Create(driftOperation),
                ModuleFileApply: new(ModuleFileAction.Write, fileRoot, configPath, fileAbsent, Sha("other"),
                    Encoding.UTF8.GetByteCount("other"), Convert.ToBase64String(Encoding.UTF8.GetBytes("other")), 420)));
            check(!drift.Success && drift.ProblemCode == PrivilegedProblemCode.PlanChanged
                && await File.ReadAllTextAsync(configPath) == content,
                "Linux Helper refuses a file publication whose frozen observation no longer matches");

            var replay = await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleFileApply,
                OperationId: fileOperation, Correlation: CorrelationContext.Create(fileOperation),
                ModuleFileApply: new(ModuleFileAction.Write, fileRoot, configPath, observedFile, contentSha,
                    Encoding.UTF8.GetByteCount(content), Convert.ToBase64String(Encoding.UTF8.GetBytes(content)), 420)));
            check(!replay.Success && replay.ProblemCode == PrivilegedProblemCode.Conflict,
                "Linux Helper refuses to replay an already attempted file operation");

            var deleteOperation = Guid.NewGuid();
            var delete = await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleFileApply,
                OperationId: deleteOperation, Correlation: CorrelationContext.Create(deleteOperation),
                ModuleFileApply: new(ModuleFileAction.Delete, fileRoot, configPath, observedFile, "", 0, null, observedFile.Mode)));
            check(delete.Success && !File.Exists(configPath),
                "Linux Helper deletes only the confirmed configuration file");

            await File.WriteAllTextAsync(Path.Combine(dataRoot, "ibdata1"), "data-one");
            Directory.CreateDirectory(Path.Combine(dataRoot, "nested"));
            await File.WriteAllTextAsync(Path.Combine(dataRoot, "nested", "undo.log"), "data-two");
            var dataQuery = await QueryAsync(PrivilegedOperationKind.ModuleDataQuery,
                moduleDataQuery: new(dataRoot, ModuleDataAction.Backup));
            var dataState = dataQuery.ModuleData;
            check(dataQuery.Success && dataState is { Exists: true, IsDirectory: true, Files: 2 }
                && dataState.Bytes > 0 && dataState.Digest.Length == 64,
                "Linux Helper freezes a structural digest over every data file");

            var backupOperation = Guid.NewGuid();
            var backup = await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleDataApply,
                OperationId: backupOperation, Correlation: CorrelationContext.Create(backupOperation),
                ModuleDataApply: new(ModuleDataAction.Backup, dataRoot, dataState, scope, backupRef)));
            var backupDirectory = Path.Combine(StateRoot, "backups", scope, backupRef);
            var manifestPath = Path.Combine(backupDirectory, "manifest.json");
            check(backup.Success && File.Exists(manifestPath)
                && await File.ReadAllTextAsync(Path.Combine(backupDirectory, "ibdata1")) == "data-one"
                && await File.ReadAllTextAsync(Path.Combine(backupDirectory, "nested", "undo.log")) == "data-two",
                "Linux Helper writes a verified backup under its protected state root");

            var preserveOperation = Guid.NewGuid();
            var preserve = await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleDataApply,
                OperationId: preserveOperation, Correlation: CorrelationContext.Create(preserveOperation),
                ModuleDataApply: new(ModuleDataAction.Preserve, dataRoot, dataState, scope, null)));
            check(preserve.Success && File.Exists(Path.Combine(StateRoot, "preserved", scope, dataState.Digest + ".json"))
                && File.Exists(Path.Combine(dataRoot, "ibdata1")),
                "Linux Helper records a preservation receipt without modifying the data root");

            var deleteDataOperation = Guid.NewGuid();
            var deleteData = await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleDataApply,
                OperationId: deleteDataOperation, Correlation: CorrelationContext.Create(deleteDataOperation),
                ModuleDataApply: new(ModuleDataAction.Delete, dataRoot, dataState, scope, null)));
            check(deleteData.Success && Directory.Exists(dataRoot)
                && !Directory.EnumerateFileSystemEntries(dataRoot).Any(),
                "Linux Helper empties only the confirmed data root");

            var replayData = await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleDataApply,
                OperationId: backupOperation, Correlation: CorrelationContext.Create(backupOperation),
                ModuleDataApply: new(ModuleDataAction.Backup, dataRoot, dataState, scope, backupRef)));
            check(!replayData.Success && replayData.ProblemCode == PrivilegedProblemCode.Conflict,
                "Linux Helper refuses to replay an already attempted data operation");

            await WritePolicyAsync(fileRoot, dataRoot, revokeData: true);
            var revoked = await QueryAsync(PrivilegedOperationKind.ModuleDataQuery,
                moduleDataQuery: new(dataRoot, ModuleDataAction.Backup));
            check(!revoked.Success && revoked.ProblemCode == PrivilegedProblemCode.AccessDenied,
                "a revoked data root stops resolving through the root-owned policy");
            var revokedApply = await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleDataApply,
                OperationId: Guid.NewGuid(), Correlation: CorrelationContext.Create(),
                ModuleDataApply: new(ModuleDataAction.Backup, dataRoot, dataState, scope, "revoked")));
            check(!revokedApply.Success && revokedApply.ProblemCode == PrivilegedProblemCode.PlanChanged,
                "a revoked data root makes an already confirmed data plan a changed plan");

            Directory.Delete(backupDirectory, recursive: true);
            Directory.Delete(Path.Combine(StateRoot, "preserved", scope), recursive: true);
            File.Delete(PolicyPath);
            Directory.Delete(baseRoot, recursive: true);
        }
        finally
        {
            if (originalPolicy is not null) await File.WriteAllBytesAsync(PolicyPath, originalPolicy);
            else if (File.Exists(PolicyPath)) File.Delete(PolicyPath);
            try { if (Directory.Exists(baseRoot)) Directory.Delete(baseRoot, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task WritePolicyAsync(string fileRoot, string dataRoot, bool revokeData = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PolicyPath)!);
        var document = new
        {
            schemaVersion = 1,
            roots = revokeData
                ? new object[]
                {
                    new
                    {
                        kind = "file", root = fileRoot, actions = new[] { "write", "delete" },
                        maximumBytes = 1048576, mode = 420, owner = 0, group = 0,
                    },
                }
                : new object[]
                {
                    new
                    {
                        kind = "file", root = fileRoot, actions = new[] { "write", "delete" },
                        maximumBytes = 1048576, mode = 420, owner = 0, group = 0,
                    },
                    new
                    {
                        kind = "data", root = dataRoot, actions = new[] { "preserve", "backup", "delete" },
                        maximumBytes = 1073741824,
                    },
                },
        };
        await File.WriteAllTextAsync(PolicyPath, JsonSerializer.Serialize(document));
        File.SetUnixFileMode(PolicyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request) =>
        await modulehelper::PrivilegedOperationExecutor.ExecuteAsync(request, HelperPolicy);

    private static Task<PrivilegedOperationResult> QueryAsync(PrivilegedOperationKind operation,
        ModuleFileQuery? moduleFileQuery = null, ModuleDataQuery? moduleDataQuery = null)
    {
        var operationId = Guid.NewGuid();
        return ExecuteAsync(new PrivilegedOperationRequest(operation, OperationId: operationId,
            Correlation: CorrelationContext.Create(operationId), ModuleFileQuery: moduleFileQuery,
            ModuleDataQuery: moduleDataQuery));
    }

    private static string Sha(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
