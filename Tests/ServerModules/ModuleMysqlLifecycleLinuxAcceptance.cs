extern alias modulehelper;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.Privileged;

/// <summary>
/// Disposable Ubuntu acceptance for the DC-03 composed MySQL lifecycle: publish a configuration file
/// through the module file adapter, reload the real MySQL service, back up and preserve the declared
/// data root, remove the package while preserving configuration/data, then delete the data through the
/// module data adapter. The package and service wrappers are host operators here; the privileged
/// file/data actions are the closed Helper operations a module would use.
/// </summary>
internal static class ModuleMysqlLifecycleLinuxAcceptance
{
    private const string PolicyPath = "/etc/relaxkonos/module-resources.json";
    private const string StateRoot = "/var/lib/relaxkonos/module-resources";
    private const string FileRoot = "/etc/mysql/mysql.conf.d";
    private const string DataRoot = "/var/lib/mysql";
    private const string ConfigPath = FileRoot + "/relaxkonos-dc03.cnf";
    private const string Scope = "mysql-acceptance";
    private const string BackupRef = "pre-uninstall";
    private const string Content = "[mysqld]\nmax_connections=123\n";
    private static readonly modulehelper::PrivilegedOperationPolicy HelperPolicy = new([], [], [], [], null);

    internal static async Task RunAsync(string[] args, Action<bool, string> check)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("SKIP: MySQL lifecycle acceptance requires Linux.");
            return;
        }
        if (!Environment.IsPrivilegedProcess)
            throw new InvalidOperationException("The MySQL lifecycle acceptance must run as root.");
        foreach (var binary in new[] { "/usr/bin/apt-get", "/usr/sbin/service", "/usr/bin/dpkg-query" })
            if (!File.Exists(binary)) throw new InvalidOperationException("MySQL lifecycle acceptance requires " + binary);
        var install = args.Contains("--install", StringComparer.Ordinal);
        try
        {
            if (install)
            {
                await RunAsync("/usr/bin/apt-get", ["update"], 600);
                var installResult = await RunAsync("/usr/bin/apt-get", ["install", "-y", "mysql-server"], 1200);
                check(installResult.ExitCode == 0, "real apt installs mysql-server in the disposable host");
            }
            if (!File.Exists("/usr/bin/mysql"))
                throw new InvalidOperationException("MySQL lifecycle acceptance requires /usr/bin/mysql; pass --install.");
            var start = await RunAsync("/usr/sbin/service", ["mysql", "start"], 120);
            check(start.ExitCode == 0, "real MySQL service starts before configuration publication");
            check(await MysqlVariableAsync("max_connections") is { } initial && initial != "123",
                "the initial MySQL value differs from the value the configuration file will set");

            await WritePolicyAsync();
            var absent = await QueryFileAsync();
            var fileAbsent = absent.ModuleFile;
            check(absent.Success && fileAbsent is { Exists: false },
                "Helper observes an absent MySQL configuration file through its approved root");
            var fileWrite = Guid.NewGuid();
            var write = await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleFileApply,
                OperationId: fileWrite, Correlation: CorrelationContext.Create(fileWrite),
                ModuleFileApply: new(ModuleFileAction.Write, FileRoot, ConfigPath, fileAbsent,
                    Sha(Content), Encoding.UTF8.GetByteCount(Content), Convert.ToBase64String(Encoding.UTF8.GetBytes(Content)),
                    420)));
            check(write.Success && File.Exists(ConfigPath) && await File.ReadAllTextAsync(ConfigPath) == Content,
                "Helper publishes the exact MySQL configuration file with the policy mode");

            var restart = await RunAsync("/usr/sbin/service", ["mysql", "restart"], 120);
            check(restart.ExitCode == 0 && await MysqlVariableAsync("max_connections") == "123",
                "the real MySQL instance reloads and exposes the published configuration value");
            check((await RunAsync("/usr/sbin/service", ["mysql", "stop"], 120)).ExitCode == 0,
                "the real MySQL service stops before data is frozen and the package is removed");

            var dataQuery = await QueryDataAsync();
            var dataState = dataQuery.ModuleData;
            check(dataQuery.Success && dataState is { Exists: true, IsDirectory: true } && dataState.Files > 0 && dataState.Bytes > 0,
                "Helper freezes the exact MySQL data-root digest before uninstall");
            var backup = await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleDataApply,
                OperationId: Guid.NewGuid(), Correlation: CorrelationContext.Create(),
                ModuleDataApply: new(ModuleDataAction.Backup, DataRoot, dataState!, Scope, BackupRef)));
            check(backup.Success && File.Exists(Path.Combine(StateRoot, "backups", Scope, BackupRef, "manifest.json")),
                "Helper writes a verified MySQL data backup before uninstall");
            var preserve = await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleDataApply,
                OperationId: Guid.NewGuid(), Correlation: CorrelationContext.Create(),
                ModuleDataApply: new(ModuleDataAction.Preserve, DataRoot, dataState!, Scope, null)));
            check(preserve.Success && File.Exists(Path.Combine(StateRoot, "preserved", Scope, dataState!.Digest + ".json"))
                && Directory.EnumerateFileSystemEntries(DataRoot).Any(),
                "Helper records the MySQL data preservation without changing the live data root");

            var remove = await RunAsync("/usr/bin/apt-get", ["remove", "-y", "mysql-server"], 900);
            check(remove.ExitCode == 0 && !await InstalledAsync("mysql-server"),
                "real apt removes mysql-server without purging configuration or data");
            check(Directory.Exists(DataRoot) && Directory.EnumerateFileSystemEntries(DataRoot).Any()
                && File.Exists(ConfigPath),
                "the preserved data root and published configuration survive the package removal");

            var delete = await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleDataApply,
                OperationId: Guid.NewGuid(), Correlation: CorrelationContext.Create(),
                ModuleDataApply: new(ModuleDataAction.Delete, DataRoot, dataState!, Scope, null)));
            check(delete.Success && Directory.Exists(DataRoot) && !Directory.EnumerateFileSystemEntries(DataRoot).Any(),
                $"an explicit confirmed module data delete empties only the declared MySQL data root "
                + $"(problem={delete.ProblemCode}, error={delete.Error})");
            var deleteFile = await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleFileApply,
                OperationId: Guid.NewGuid(), Correlation: CorrelationContext.Create(),
                ModuleFileApply: new(ModuleFileAction.Delete, FileRoot, ConfigPath,
                    (await QueryFileAsync()).ModuleFile!, "", 0, null, 420)));
            check(deleteFile.Success && !File.Exists(ConfigPath),
                "the published configuration file is deleted only through the confirmed module action");
            Directory.Delete(Path.Combine(StateRoot, "backups", Scope), recursive: true);
            Directory.Delete(Path.Combine(StateRoot, "preserved", Scope), recursive: true);
        }
        finally
        {
            File.Delete(PolicyPath);
        }
    }

    private static async Task<PrivilegedOperationResult> QueryFileAsync()
    {
        var id = Guid.NewGuid();
        return await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleFileQuery,
            OperationId: id, Correlation: CorrelationContext.Create(id),
            ModuleFileQuery: new(FileRoot, ConfigPath, ModuleFileAction.Write)));
    }

    private static async Task<PrivilegedOperationResult> QueryDataAsync()
    {
        var id = Guid.NewGuid();
        return await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleDataQuery,
            OperationId: id, Correlation: CorrelationContext.Create(id),
            ModuleDataQuery: new(DataRoot, ModuleDataAction.Backup)));
    }

    private static Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request) =>
        modulehelper::PrivilegedOperationExecutor.ExecuteAsync(request, HelperPolicy);

    private static async Task WritePolicyAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PolicyPath)!);
        var document = new
        {
            schemaVersion = 1,
            roots = new object[]
            {
                new { kind = "file", root = FileRoot, actions = new[] { "write", "delete" },
                    maximumBytes = 1048576, mode = 420, owner = 0, group = 0 },
                new { kind = "data", root = DataRoot, actions = new[] { "preserve", "backup", "delete" },
                    maximumBytes = 1073741824 },
            },
        };
        await File.WriteAllTextAsync(PolicyPath, JsonSerializer.Serialize(document));
        File.SetUnixFileMode(PolicyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task<string?> MysqlVariableAsync(string name)
    {
        var result = await RunAsync("/usr/bin/mysql",
            ["--protocol=socket", "-uroot", "-N", "-B", "-e", $"SHOW VARIABLES LIKE '{name}';"], 60);
        if (result.ExitCode != 0) return null;
        var fields = result.Output.Split('\t', StringSplitOptions.TrimEntries);
        return fields.Length == 2 ? fields[1] : null;
    }

    private static async Task<bool> InstalledAsync(string package)
    {
        var result = await RunAsync("/usr/bin/dpkg-query",
            ["-W", "-f=${Status}\t${Version}\n", package], 30);
        return result.ExitCode == 0 && result.Output.StartsWith("install ok installed\t", StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, string[] arguments, int timeoutSeconds)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + file);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (124, "timeout");
        }
        return (process.ExitCode, (await output) + (await error));
    }

    private static string Sha(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
