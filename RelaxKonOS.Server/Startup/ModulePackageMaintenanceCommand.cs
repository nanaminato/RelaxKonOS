using System.Runtime.InteropServices;
using System.Text.Json;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.Server.Startup;

/// <summary>Offline inspection only. It cannot accept a catalog or install a module.</summary>
internal static class ModulePackageMaintenanceCommand
{
    internal const string HostApiVersion = "1.0.0";
    internal static async Task<int> RunAsync(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i += 2)
            if (i + 1 >= args.Length || args[i] is not ("--content-root" or "--source-id" or "--catalog" or "--archive" or "--release-id")
                || !values.TryAdd(args[i], args[i + 1])) throw new ModulePackageException("module-package-command-invalid");
        if (values.Count != 5 || !values.TryGetValue("--content-root", out var root)
            || !Path.IsPathFullyQualified(root) || !Directory.Exists(root)) throw new ModulePackageException("module-package-command-invalid");
        root = Path.GetFullPath(root);
        var sourceId = values["--source-id"];
        // A source ID is also used as a local history file name. Reject separators before any access.
        if (sourceId.Length is 0 or > 96 || sourceId[0] is < 'a' or > 'z'
            || !sourceId.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-'))
            throw new ModulePackageException("module-package-source-invalid");
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environments.Production;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root, EnvironmentName = environment });
        using var configurationLifetime = (IDisposable)builder.Configuration;
        builder.Configuration.AddJsonFile("appsettings.host.json", optional: true, reloadOnChange: false);
        var policy = builder.Configuration.GetSection("ModulePackages:Sources:" + sourceId).Get<ModulePackageTrustPolicy>()
            ?? throw new ModulePackageException("module-package-source-unconfigured");
        if (policy.SourceId != sourceId) throw new ModulePackageException("module-package-source-invalid");
        var identity = ModuleManagementStore.ReadRecoveryReport(root);
        var context = new ModulePackageContext(identity.ServiceId, RuntimeInformation.RuntimeIdentifier, HostApiVersion,
            builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("ModulePackages:DevelopmentInstance"));
        var historyPath = Path.Combine(root, "data", "module-packages", "trust-history", sourceId + ".json");
        var historyPresent = File.Exists(historyPath);
        var history = historyPresent
            ? ModulePackageJson.Read<ModulePackageTrustHistory>(await ReadBoundedAsync(historyPath, 32 * 1024 * 1024), 32 * 1024 * 1024)
            : ModulePackageTrustHistory.Empty(policy, context);
        var envelope = await ReadBoundedAsync(values["--catalog"], 6 * 1024 * 1024);
        await using var archive = new FileStream(values["--archive"], FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var result = await ModulePackageVerifier.VerifyCandidateAsync(archive, envelope, policy, context, history,
            values["--release-id"], DateTimeOffset.UtcNow);
        Console.WriteLine(JsonSerializer.Serialize(new { readOnly = true, trustHistoryPresent = historyPresent,
            trustHistoryCommitted = false, installed = false, candidate = result }, ModulePackageJson.Options));
        return 0;
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
