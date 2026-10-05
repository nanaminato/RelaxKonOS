using System.Security.Principal;
using System.Text.Json;
using RelaxKonOS.Protocol.FileServices;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.Tunnels;
using RelaxKonOS.Server.FileServices;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Platform;
using RelaxKonOS.Server.Runtimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.WebServer;

namespace RelaxKonOS.Server.Installations;

public sealed record ComponentCleanupResult(string Component, bool Succeeded, string? ProblemCode);
public sealed record ComponentCleanupReceipt(bool Succeeded, IReadOnlyList<ComponentCleanupResult> Components);

/// <summary>Sequential, local maintenance. A failed step prevents deletion of ownership data.</summary>
public static class ManagedComponentCleanup
{
    public static bool IsAuthorized() => OperatingSystem.IsLinux()
        ? UserExecution.ServerProcessIdentity.IsPrivileged()
        : OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static async Task<ComponentCleanupReceipt> RunAsync(
        IEnumerable<(string Name, Func<CancellationToken, Task<string?>> Execute)> steps,
        string receiptPath, CancellationToken ct)
    {
        var results = new List<ComponentCleanupResult>();
        foreach (var step in steps)
        {
            string? problem;
            try { problem = await step.Execute(ct); }
            catch (Exception) { problem = "deployment.component_cleanup_failed"; }
            results.Add(new(step.Name, string.IsNullOrEmpty(problem), problem));
            var receipt = new ComponentCleanupReceipt(results.All(x => x.Succeeded), results.ToArray());
            var directory = Path.GetDirectoryName(Path.GetFullPath(receiptPath))!;
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var temporary = receiptPath + ".new";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(receipt), ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, receiptPath, overwrite: true);
            if (!receipt.Succeeded) return receipt;
        }
        return new(true, results);
    }

    public static Task<ComponentCleanupReceipt> RemoveAsync(IServiceProvider services, string receiptPath, string dataRoot, bool personal, CancellationToken ct, string components = "smb,nginx,frp,mihomo")
    {
        var selected = components.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (selected.Length != selected.Distinct(StringComparer.Ordinal).Count() ||
            selected.Any(x => x is not ("smb" or "nginx" or "frp" or "mihomo")))
            throw new ArgumentException("Invalid component selection.");
        IEnumerable<(string Name, Func<CancellationToken, Task<string?>> Execute)> steps = [
            ("smb", token => personal ? Task.FromResult<string?>(null) : RemoveSharesAsync(services, dataRoot, token)),
            ("nginx", token => services.GetRequiredService<NginxWebServerManager>().RemoveOwnedInstallationAsync(personal, token)),
            ("frp", async token => {
                var runtime = services.GetRequiredService<IRuntimeManager>();
                // The independent service can outlive Server's runtime pointer. Always ask
                // the constrained Helper to remove owned instances before clearing metadata.
                var removed = await services.GetRequiredService<ManagedRuntimeOperations>().ExecuteAsync(
                    new(Protocol.Privileged.ManagedRuntime.Frpc, Protocol.Privileged.ManagedRuntimeAction.Uninstall), token);
                if (!removed.Success) return ManagedRuntimeOperations.Problem(removed);
                if ((await runtime.GetManagedFrpcStatusAsync(token)).State == TunnelRuntimeState.NotInstalled) return null;
                var result = await runtime.UninstallManagedFrpcAsync(token);
                return result.Succeeded ? null : result.ProblemCode;
            }),
            ("mihomo", async token => {
                var runtime = services.GetRequiredService<IProxyRuntimeManager>();
                var paths = services.GetRequiredService<IProxyPlatformPaths>();
                // Maintenance must not contact the controller or decrypt credentials under
                // the administrator's key ring. Runtime ownership is persisted locally.
                if (!File.Exists(Path.Combine(paths.GetStateDirectory(), "mihomo-runtime.json"))) {
                    var versions = paths.GetEngineVersionsDirectory("mihomo");
                    return Directory.Exists(versions) && Directory.EnumerateFileSystemEntries(versions).Any()
                        ? "deployment.component_ownership_missing" : null;
                }
                var settings = await services.GetRequiredService<IProxySettingsService>().GetAsync(token);
                var componentDirectories = new[] { paths.GetProtectedConfigurationDirectory(),
                    paths.GetEngineDataDirectory("mihomo"), paths.GetSanitizedLogDirectory(), paths.GetStateDirectory() };
                foreach (var directory in componentDirectories) RequirePlainTree(directory);
                if (settings.SystemProxyEnabled) {
                    var problem = await services.GetRequiredService<IHostSystemProxyService>().ApplyAsync(
                        settings with { SystemProxyEnabled = false }, settings, true, token);
                    if (!string.IsNullOrEmpty(problem)) return problem;
                }
                var result = await runtime.UninstallManagedAsync("mihomo", token);
                if (!string.IsNullOrEmpty(result.ProblemCode)) return result.ProblemCode;
                foreach (var directory in componentDirectories)
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                return null;
            })
        ];
        return RunAsync(steps.Where(x => selected.Contains(x.Name, StringComparer.Ordinal)), receiptPath, ct);
    }

    private static void RequirePlainTree(string root)
    {
        if (!Directory.Exists(root)) return;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unsafe managed component directory.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Unsafe managed component entry.");
            if ((attributes & FileAttributes.Directory) != 0) RequirePlainTree(entry);
        }
    }

    private static async Task<string?> RemoveSharesAsync(IServiceProvider services, string dataRoot, CancellationToken ct)
    {
        var provider = services.GetServices<IFileServiceProvider>().Single(x => x.IsApplicable);
        // A missing Linux include means we own no shares. Do not probe or change an
        // unrelated Samba installation (including systems without Samba installed).
        if (OperatingSystem.IsLinux() && !File.Exists("/etc/samba/relaxkonos.conf")) return null;
        var shares = await provider.ListSharesAsync(ct);
        if (shares.Any(share => SharedPathOverlapsDataRoot(share.Path, dataRoot))) return "deployment.shared_data_inside_data_root";
        if (OperatingSystem.IsWindows())
        {
            var ledger = services.GetRequiredService<IWindowsSmbOwnershipLedger>();
            var owned = await ledger.ListAsync(ct);
            if (owned.Keys.Any(id => !shares.Any(share => share.Id == id))) return FileServiceProblemCodes.ReconciliationRequired;
        }
        foreach (var share in shares.Where(x => x.Managed))
        {
            if (share.Drifted) return FileServiceProblemCodes.ReconciliationRequired;
            var result = await provider.DeleteShareAsync(share.Id, Guid.NewGuid(), ct);
            if (!result.Succeeded) return result.ProblemCode ?? "deployment.component_cleanup_failed";
        }
        return null;
    }

    internal static bool SharedPathOverlapsDataRoot(string sharePath, string dataRoot)
    {
        var root = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(sharePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || root.StartsWith(path + Path.DirectorySeparatorChar, comparison);
    }
}
