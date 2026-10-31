using System.Text.Json;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Server.Startup;

internal static class ModulePackageOperationState
{
    internal const string AwaitingRestart = "AwaitingRestart";
    internal const string Succeeded = "Succeeded";
    internal const string Failed = "Failed";
}

internal sealed record ModulePackageOperation(Guid OperationId, string Action, string ModuleId, string Version,
    string ReleaseId, string ArtifactSha256, string? PreviousVersion, string State, string? ProblemCode,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? ProcessGeneration);

internal sealed record ModulePackageOperationDocument(int SchemaVersion, ModulePackageOperation[] Operations);

/// <summary>Tracks install/rollback intents until the next Server startup can verify the installed fact.</summary>
internal static class ModulePackageOperationStore
{
    private const int Limit = 1024 * 1024;
    private const int Maximum = 1000;
    internal static string PathFor(string contentRoot) =>
        Path.Combine(contentRoot, "data", "module-management", "package-operations.json");

    internal static IReadOnlyList<ModulePackageOperation> Read(string contentRoot)
    {
        var path = PathFor(contentRoot);
        if (!File.Exists(path)) return [];
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > Limit) throw Invalid();
        ModulePackageOperationDocument? document;
        try { document = JsonSerializer.Deserialize<ModulePackageOperationDocument>(File.ReadAllBytes(path), RelaxKonOSJsonOptions.Default); }
        catch (JsonException) { throw Invalid(); }
        if (document is null || document.SchemaVersion != 1 || document.Operations is not { Length: <= Maximum }
            || document.Operations.Select(operation => operation.OperationId).Distinct().Count() != document.Operations.Length
            || document.Operations.Any(operation => operation is null || operation.OperationId == Guid.Empty
                || operation.Action is not ("install" or "rollback") || !Identifier(operation.ModuleId)
                || !Version(operation.Version) || !Identifier(operation.ReleaseId) || !Digest(operation.ArtifactSha256)
                || operation.PreviousVersion is not null && !Version(operation.PreviousVersion)
                || operation.State is not (ModulePackageOperationState.AwaitingRestart
                    or ModulePackageOperationState.Succeeded or ModulePackageOperationState.Failed)
                || operation.CreatedAt == default || operation.UpdatedAt < operation.CreatedAt))
            throw Invalid();
        return document.Operations;
    }

    internal static ModulePackageOperation Record(string contentRoot, string action, InstalledModuleRecord target,
        string? previousVersion, DateTimeOffset now)
    {
        var operations = Read(contentRoot).ToList();
        var operation = new ModulePackageOperation(Guid.NewGuid(), action, target.ModuleId, target.Version,
            target.ReleaseId, target.ArtifactSha256, previousVersion, ModulePackageOperationState.AwaitingRestart,
            null, now, now, null);
        if (operations.Count >= Maximum) throw new ModulePackageException("module-package-operation-capacity");
        operations.Add(operation);
        Write(contentRoot, operations);
        return operation;
    }

    internal static ModulePackageOperation Update(string contentRoot, Guid operationId, string state, string? problemCode,
        string? processGeneration, DateTimeOffset now)
    {
        var operations = Read(contentRoot).ToList();
        var index = operations.FindIndex(operation => operation.OperationId == operationId);
        if (index < 0) throw new ModulePackageException("module-package-operation-not-found");
        if (state is not (ModulePackageOperationState.Succeeded or ModulePackageOperationState.Failed))
            throw new ModulePackageException("module-package-operation-state-invalid");
        var updated = operations[index] with { State = state, ProblemCode = problemCode, ProcessGeneration = processGeneration, UpdatedAt = now };
        operations[index] = updated;
        Write(contentRoot, operations);
        return updated;
    }

    /// <summary>Called after the Host is healthy: a pending intent becomes Succeeded only when the
    /// installed fact still names the exact release it asked for.</summary>
    internal static IReadOnlyList<ModulePackageOperation> VerifyStartup(string contentRoot,
        IReadOnlyList<InstalledModuleRecord> installed, string processGeneration, DateTimeOffset now)
    {
        var operations = Read(contentRoot).ToList();
        var byModule = installed.ToDictionary(record => record.ModuleId, StringComparer.Ordinal);
        var changed = false;
        for (var i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
            if (operation.State != ModulePackageOperationState.AwaitingRestart) continue;
            var matched = byModule.TryGetValue(operation.ModuleId, out var current)
                && current.Version == operation.Version && current.ReleaseId == operation.ReleaseId
                && current.ArtifactSha256 == operation.ArtifactSha256;
            var updated = operation with
            {
                State = matched ? ModulePackageOperationState.Succeeded : ModulePackageOperationState.Failed,
                ProblemCode = matched ? null : "module-package-operation-target-mismatch",
                ProcessGeneration = processGeneration,
                UpdatedAt = now,
            };
            operations[i] = updated;
            changed = true;
        }
        if (changed) Write(contentRoot, operations);
        return operations;
    }

    private static void Write(string contentRoot, IReadOnlyList<ModulePackageOperation> operations)
    {
        if (operations.Count > Maximum) throw new ModulePackageException("module-package-operation-capacity");
        var path = PathFor(contentRoot);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new ModulePackageOperationDocument(1, operations.ToArray()), RelaxKonOSJsonOptions.Default);
            if (bytes.Length > Limit) throw new ModulePackageException("module-package-operation-capacity");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       65536, FileOptions.WriteThrough))
            {
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (OperatingSystem.IsLinux() && Environment.UserName == "root")
            {
                var state = Path.Combine(directory, "state.json");
                if (File.Exists(state)) ModuleManagementStore.PreserveUnixOwner(temporary, state);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool Identifier(string? value) => value is { Length: > 0 and <= 96 } && value[0] is >= 'a' and <= 'z'
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
    private static bool Version(string? value) => System.Version.TryParse(value, out var parsed)
        && parsed.Build >= 0 && parsed.Revision == -1 && parsed.ToString(3) == value;
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    private static ModulePackageException Invalid() => new("module-package-operation-state-invalid");
}
