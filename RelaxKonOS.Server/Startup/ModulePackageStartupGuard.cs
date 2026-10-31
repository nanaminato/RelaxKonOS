using System.Text.Json;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Server.Startup;

/// <summary>
/// A startup marker distinguishes "the previous process reached ApplicationStarted" from "it failed
/// while loading the installed module set". If the marker survived, an update operation with a
/// previous version is restored before any module assembly is loaded.
/// </summary>
internal static class ModulePackageStartupGuard
{
    internal sealed record Marker(int SchemaVersion, string ProcessGeneration, DateTimeOffset StartedAt);
    internal static string PathFor(string contentRoot) =>
        Path.Combine(contentRoot, "data", "module-management", "package-startup.json");

    internal static void Begin(string contentRoot, string processGeneration)
    {
        var marker = PathFor(contentRoot);
        if (File.Exists(marker)) Recover(contentRoot, processGeneration);
        var directory = Path.GetDirectoryName(marker)!;
        Directory.CreateDirectory(directory);
        var temporary = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Marker(1, processGeneration, DateTimeOffset.UtcNow),
                RelaxKonOSJsonOptions.Default);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, marker, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void Complete(string contentRoot)
    {
        try { File.Delete(PathFor(contentRoot)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void Recover(string contentRoot, string processGeneration)
    {
        var operations = ModulePackageOperationStore.Read(contentRoot)
            .Where(operation => operation.State == ModulePackageOperationState.AwaitingRestart)
            .ToArray();
        foreach (var operation in operations)
        {
            if (operation.PreviousVersion is null)
            {
                Quarantine(contentRoot, operation, "module-package-operation-startup-quarantined", processGeneration);
                continue;
            }
            try
            {
                var previous = InstalledModuleStore.ReadInstallMetadata(contentRoot,
                    "packages/" + operation.ModuleId + "/" + operation.PreviousVersion);
                if (previous.ModuleId != operation.ModuleId || previous.Version != operation.PreviousVersion)
                    throw new ModulePackageException("module-package-operation-startup-rollback-failed");
                InstalledModuleStore.VerifyPackage(contentRoot, previous);
                var modules = InstalledModuleStore.Read(contentRoot).ToList();
                var index = modules.FindIndex(module => module.ModuleId == operation.ModuleId);
                if (index >= 0) modules[index] = previous; else modules.Add(previous);
                InstalledModuleStore.Write(contentRoot, modules);
                try
                {
                    ModulePackageOperationStore.Update(contentRoot, operation.OperationId,
                        ModulePackageOperationState.Failed, "module-package-operation-startup-rollback",
                        processGeneration, DateTimeOffset.UtcNow);
                }
                catch (ModulePackageException) { }
            }
            catch (Exception error) when (error is ModulePackageException or IOException or UnauthorizedAccessException)
            {
                Quarantine(contentRoot, operation, "module-package-operation-startup-rollback-failed-quarantined",
                    processGeneration);
            }
        }
    }

    private static void Quarantine(string contentRoot, ModulePackageOperation operation, string problemCode,
        string processGeneration)
    {
        try
        {
            var modules = InstalledModuleStore.Read(contentRoot).ToList();
            var index = modules.FindIndex(module => module.ModuleId == operation.ModuleId);
            if (index >= 0)
            {
                modules[index] = modules[index] with { Quarantined = true };
                InstalledModuleStore.Write(contentRoot, modules);
            }
            ModulePackageOperationStore.Update(contentRoot, operation.OperationId,
                ModulePackageOperationState.Failed, problemCode, processGeneration, DateTimeOffset.UtcNow);
        }
        catch (Exception error) when (error is ModulePackageException or IOException or UnauthorizedAccessException) { }
    }
}
