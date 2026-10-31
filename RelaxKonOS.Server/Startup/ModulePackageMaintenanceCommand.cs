using System.Text.Json;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.UserExecution;

namespace RelaxKonOS.Server.Startup;

/// <summary>Offline package inspection, catalog acceptance and v1 official module installation.</summary>
internal static class ModulePackageMaintenanceCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2 || args[1] is not ("verify-package" or "catalog-status" or "accept-catalog"
                or "install-package" or "list-installed" or "list-versions" or "list-package-operations"
                or "rollback-installed" or "remove-installed"))
            throw new ModulePackageException("module-package-command-invalid");
        var command = args[1];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i += 2)
            if (i + 1 >= args.Length || args[i] is not ("--content-root" or "--source-id" or "--catalog" or "--archive" or "--release-id"
                    or "--service-id" or "--expected-revision" or "--idempotency-key" or "--module-id" or "--version")
                || !values.TryAdd(args[i], args[i + 1])) throw new ModulePackageException("module-package-command-invalid");
        if (!values.TryGetValue("--content-root", out var root)
            || !Path.IsPathFullyQualified(root) || !Directory.Exists(root)) throw new ModulePackageException("module-package-command-invalid");
        root = Path.GetFullPath(root);
        var identity = ModuleManagementStore.ReadRecoveryReport(root);
        if (command == "catalog-status")
        {
            if (values.Count != 1) throw new ModulePackageException("module-package-command-invalid");
            var state = ModulePackageCatalogStore.Read(root, identity.ServiceId);
            Console.WriteLine(JsonSerializer.Serialize(new { readOnly = true, state.ServiceId, state.Revision,
                observationScope = "persisted-catalogs", sources = state.Sources.Select(p => new {
                    sourceId = p.Key, p.Value.History.Sequence, p.Value.History.PolicyRevision }) }, ModulePackageJson.Options));
            return 0;
        }
        if (command == "list-installed")
        {
            if (values.Count != 1) throw new ModulePackageException("module-package-command-invalid");
            Console.WriteLine(JsonSerializer.Serialize(new { readOnly = true,
                modules = InstalledModuleStore.Read(root) }, ModulePackageJson.Options));
            return 0;
        }
        if (command == "list-versions")
        {
            if (values.Count != 2 || !values.TryGetValue("--module-id", out var moduleId))
                throw new ModulePackageException("module-package-command-invalid");
            Console.WriteLine(JsonSerializer.Serialize(new { readOnly = true, moduleId,
                versions = InstalledModuleStore.ReadVersions(root, moduleId) }, ModulePackageJson.Options));
            return 0;
        }
        if (command == "list-package-operations")
        {
            if (values.Count != 1) throw new ModulePackageException("module-package-command-invalid");
            Console.WriteLine(JsonSerializer.Serialize(new { readOnly = true,
                operations = ModulePackageOperationStore.Read(root) }, ModulePackageJson.Options));
            return 0;
        }
        if (command == "rollback-installed")
        {
            if (values.Count != 3 || !values.TryGetValue("--module-id", out var moduleId)
                || !values.TryGetValue("--version", out var version))
                throw new ModulePackageException("module-package-command-invalid");
            if (!AuthMaintenanceCommand.IsAdministrator())
                throw new ModulePackageException("module-package-local-administrator-required");
            using var hostLock = AcquireHostLock(root);
            var previous = InstalledModuleStore.Read(root).SingleOrDefault(module => module.ModuleId == moduleId);
            var record = await InstalledModuleInstaller.RollbackAsync(root, moduleId, version);
            var operation = ModulePackageOperationStore.Record(root, "rollback", record, previous?.Version, DateTimeOffset.UtcNow);
            Console.WriteLine(JsonSerializer.Serialize(new { rolledBack = true, effective = false,
                operationId = operation.OperationId, record }, ModulePackageJson.Options));
            return 0;
        }
        if (command == "remove-installed")
        {
            if (values.Count != 2 || !values.TryGetValue("--module-id", out var moduleId))
                throw new ModulePackageException("module-package-command-invalid");
            if (!AuthMaintenanceCommand.IsAdministrator())
                throw new ModulePackageException("module-package-local-administrator-required");
            if (identity.DesiredEnabledModuleIds.Contains(moduleId, StringComparer.Ordinal))
                throw new ModulePackageException("module-package-module-enabled");
            using var hostLock = AcquireHostLock(root);
            InstalledModuleStore.Remove(root, moduleId, deleteFiles: false);
            Console.WriteLine(JsonSerializer.Serialize(new { removed = true, moduleId, filesPreserved = true }, ModulePackageJson.Options));
            return 0;
        }
        var installing = command == "install-package";
        var accepting = command == "accept-catalog";
        var required = accepting
            ? new[] { "--content-root", "--source-id", "--catalog", "--service-id", "--expected-revision", "--idempotency-key" }
            : installing
                ? new[] { "--content-root", "--source-id", "--archive", "--release-id" }
                : ["--content-root", "--source-id", "--catalog", "--archive", "--release-id"];
        if (values.Count != required.Length || required.Any(key => !values.ContainsKey(key)))
            throw new ModulePackageException("module-package-command-invalid");
        if ((accepting || installing) && !AuthMaintenanceCommand.IsAdministrator())
            throw new ModulePackageException("module-package-local-administrator-required");
        if (accepting && values["--service-id"] != identity.ServiceId)
            throw new ModulePackageException("module-package-service-mismatch");
        var sourceId = values["--source-id"];
        if (!ModulePackageHostConfiguration.Identifier(sourceId))
            throw new ModulePackageException("module-package-source-invalid");
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environments.Production;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root, EnvironmentName = environment });
        using var configurationLifetime = (IDisposable)builder.Configuration;
        builder.Configuration.AddJsonFile("appsettings.host.json", optional: true, reloadOnChange: false);
        var policy = ModulePackageHostConfiguration.Source(builder.Configuration, sourceId);
        var context = ModulePackageHostConfiguration.Context(identity.ServiceId, builder.Configuration, builder.Environment);
        if (installing)
        {
            var record = await InstalledModuleInstaller.InstallAsync(root, builder.Configuration, builder.Environment,
                sourceId, values["--archive"], values["--release-id"], DateTimeOffset.UtcNow);
            var operation = ModulePackageOperationStore.Record(root, "install", record, null, DateTimeOffset.UtcNow);
            Console.WriteLine(JsonSerializer.Serialize(new { installed = true, effective = false,
                operationId = operation.OperationId, record }, ModulePackageJson.Options));
            return 0;
        }
        var envelope = await ReadBoundedAsync(values["--catalog"], 6 * 1024 * 1024);
        if (accepting)
        {
            var actor = ServerProcessIdentity.CurrentStableIdentity() ?? throw new ModulePackageException("module-package-host-identity-unavailable");
            using var store = new ModulePackageCatalogStore(root, identity.ServiceId);
            var receipt = store.Accept(envelope, policy, context, values["--expected-revision"], "local-catalog:" + actor,
                values["--idempotency-key"], DateTimeOffset.UtcNow);
            Console.WriteLine(JsonSerializer.Serialize(new { trustHistoryCommitted = true, installed = false, receipt }, ModulePackageJson.Options));
            return 0;
        }
        var catalogState = ModulePackageCatalogStore.Read(root, identity.ServiceId);
        var historyPresent = catalogState.Sources.TryGetValue(sourceId, out var savedSource);
        var history = savedSource?.History ?? ModulePackageTrustHistory.Empty(policy, context);
        await using var archive = new FileStream(values["--archive"], FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var result = await ModulePackageVerifier.VerifyCandidateAsync(archive, envelope, policy, context, history,
            values["--release-id"], DateTimeOffset.UtcNow);
        ModulePackageCatalogStore.EnsureNotRevoked(catalogState, result.Manifest.ReleaseId, result.ArchiveSha256);
        Console.WriteLine(JsonSerializer.Serialize(new { readOnly = true, trustHistoryPresent = historyPresent,
            trustHistoryCommitted = false, installed = false, candidate = result }, ModulePackageJson.Options));
        return 0;
    }

    private static FileStream AcquireHostLock(string root)
    {
        try
        {
            return new FileStream(Path.Combine(root, "data", "module-management", "host.lock"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { throw new ModulePackageException("module-package-host-lock-unavailable"); }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int limit)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 || stream.Length > limit) throw new ModulePackageException("module-package-metadata-size");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes);
        if (stream.ReadByte() != -1) throw new ModulePackageException("module-package-metadata-size");
        return bytes;
    }
}
