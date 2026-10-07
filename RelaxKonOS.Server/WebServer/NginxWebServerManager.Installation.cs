using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Server.Installations;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.Docker;

namespace RelaxKonOS.Server.WebServer;


internal sealed partial class NginxWebServerManager
{
    private async Task<WebServerOperationResult> InstallManagedCoreAsync(ManagedLayout layout, NginxManagedInstallRequest request, IWebServerOperationProgress progress, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
            return await InstallWindowsManagedAsync(layout, request, progress, cancellationToken);
        await progress.ReportAsync("installing_package", cancellationToken);
        var install = await RunInstallerAsync(layout, null, cancellationToken);
        if (!install.Success) return new WebServerOperationResult(install.ProblemCode == PrivilegedProblemCode.AccessDenied
            ? "webserver.install_elevation_required" : ToWebServerProblem(install.ProblemCode, "webserver.install_failed"));
        await progress.ReportAsync("verifying_layout", cancellationToken);
        if (!File.Exists(layout.ExecutablePath) || IsSymbolicLink(layout.ExecutablePath) || !Directory.Exists(layout.Root) || IsSymbolicLink(layout.Root))
            return new WebServerOperationResult("webserver.install_layout_invalid");
        try
        {
            await progress.ReportAsync("validating_configuration", cancellationToken);
            await File.WriteAllTextAsync(layout.MarkerPath, ManagedMarkerContent, new UTF8Encoding(false), cancellationToken);
            var test = await RunManagedConfigurationTestAsync(layout, cancellationToken);
            if (test.Success)
            {
                await progress.ReportAsync("finalizing", cancellationToken);
                return new WebServerOperationResult("");
            }
            File.Delete(layout.MarkerPath);
            return new WebServerOperationResult("webserver.config_test_failed");
        }
        catch (UnauthorizedAccessException) { return new WebServerOperationResult("webserver.install_elevation_required"); }
        catch (IOException) { return new WebServerOperationResult("webserver.install_layout_invalid"); }
    }

    private async Task<WebServerOperationResult> InstallWindowsManagedAsync(ManagedLayout layout, NginxManagedInstallRequest request, IWebServerOperationProgress progress, CancellationToken cancellationToken)
    {
        await ManagedInstallGate.WaitAsync(cancellationToken);
        try { return await InstallWindowsManagedCoreAsync(layout, request, progress, cancellationToken); }
        finally { ManagedInstallGate.Release(); }
    }

    private async Task<WebServerOperationResult> InstallWindowsManagedCoreAsync(ManagedLayout layout, NginxManagedInstallRequest request, IWebServerOperationProgress progress, CancellationToken cancellationToken)
    {
        string? packageId = null;
        try
        {
            var version = string.IsNullOrWhiteSpace(request.Version) ? "1.31.3" : request.Version.Trim();
            if (!WindowsVersionPattern().IsMatch(version)) return new("webserver.version_invalid");
            if (request.Source is not null)
            {
                await progress.ReportAsync("copying", cancellationToken);
                packageId = await packages.SaveAsync(request.Source.FileName, request.Source.Stream, cancellationToken: cancellationToken);
                if (packageId is null) return new("webserver.package_invalid");
            }
            else
            {
                await progress.ReportAsync("downloading", cancellationToken);
                using var client = await outboundProxyClients.CreateAsync(OutboundProxyTarget.RuntimeDownloads, TimeSpan.FromMinutes(5), cancellationToken);
                using var response = await client.GetAsync($"https://nginx.org/download/nginx-{version}.zip", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode) return new("webserver.install_failed");
                await using var archive = await response.Content.ReadAsStreamAsync(cancellationToken);
                packageId = await packages.SaveAsync($"nginx-{version}.zip", archive, cancellationToken: cancellationToken);
                if (packageId is null) return new("webserver.package_invalid");
            }
            await progress.ReportAsync("installing_package", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            cancellationToken = CancellationToken.None;
            var installed = await privilegedNginx.ApplyWindowsRuntimeAsync(ManagedRuntimeAction.Install,
                version, packageId is null ? null : packages.GetPath(packageId), cancellationToken);
            if (!installed.Success) return new(ToWebServerProblem(installed.ProblemCode, "webserver.install_failed"));
            await progress.ReportAsync("validating_configuration", cancellationToken);
            var verified = await privilegedNginx.ApplyWindowsRuntimeAsync(ManagedRuntimeAction.Test, cancellationToken: cancellationToken);
            return new(verified.Success ? "" : ToWebServerProblem(verified.ProblemCode, "webserver.config_test_failed"));
        }
        finally { packages.Delete(packageId); }
    }

    private async Task<WebServerOperationResult> ApplyManagedLifecycleCoreAsync(ManagedLayout layout, WebServerLifecycleAction action, CancellationToken cancellationToken)
    {
        if (UsesSystemPackageManagedService())
        {
            await StopLegacyCustomManagedInstanceAsync(layout, cancellationToken);
            var command = action switch
            {
                WebServerLifecycleAction.Start => "start",
                WebServerLifecycleAction.Stop => "stop",
                WebServerLifecycleAction.Restart => "restart",
                _ => null
            };
            if (command is not "stop")
            {
                var enable = await RunSystemdNginxOperationAsync("enable", cancellationToken);
                if (!enable.Success) return new WebServerOperationResult(ToWebServerProblem(enable.ProblemCode, $"webserver.{action.ToString().ToLowerInvariant()}_failed"));
            }
            var systemd = command is null ? new PrivilegedOperationResult(false, ProblemCode: PrivilegedProblemCode.InvalidRequest)
                : await RunSystemdNginxOperationAsync(command, cancellationToken);
            return new WebServerOperationResult(systemd.Success ? "" : ToWebServerProblem(systemd.ProblemCode, $"webserver.{action.ToString().ToLowerInvariant()}_failed"));
        }
        var result = action switch
        {
            WebServerLifecycleAction.Start => await StartManagedAsync(layout, cancellationToken),
            WebServerLifecycleAction.Stop => await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, ["-s", "quit"]), cancellationToken),
            WebServerLifecycleAction.Restart => await RestartManagedAsync(layout, cancellationToken),
            _ => new CommandResult(false, "")
        };
        return new WebServerOperationResult(result.Success ? "" : $"webserver.{action.ToString().ToLowerInvariant()}_failed");
    }

    private Task<CommandResult> RunManagedConfigurationTestAsync(ManagedLayout layout, CancellationToken cancellationToken) => UsesSystemPackageManagedService()
        ? RunSystemPackageConfigurationTestAsync(cancellationToken)
        : RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, ["-t"]), cancellationToken);

    private async Task<CommandResult> RunSystemPackageConfigurationTestAsync(CancellationToken cancellationToken)
    {
        var result = await privilegedNginx.TestConfigurationAsync(cancellationToken);
        var output = PrivilegedOutputForLog(result);
        if (result.Success)
            logger.LogInformation("Nginx system-package configuration validation succeeded.");
        else
            logger.LogWarning("Nginx system-package configuration validation failed. ProblemCode={ProblemCode}, ExitCode={ExitCode}, Error={Error}, Output={Output}",
                result.ProblemCode, result.ExitCode, result.Error, output);
        return new CommandResult(result.Success, output);
    }

    private async Task<WebServerOperationResult> UninstallManagedCoreAsync(ManagedLayout layout, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var result = await privilegedNginx.ApplyWindowsRuntimeAsync(ManagedRuntimeAction.Uninstall, cancellationToken: cancellationToken);
            return new(result.Success ? "" : ToWebServerProblem(result.ProblemCode, "webserver.uninstall_failed"));
        }
        var isManaged = IsManagedInstallation(layout);
        if (!isManaged)
        {
            var integrated = await DetectAsync(layout.ExecutablePath, null, cancellationToken);
            if (integrated?.Capabilities.CanUninstall != true)
                return new WebServerOperationResult("webserver.managed_required");
        }
        if (UsesSystemPackageManagedService())
        {
            if (isManaged) await StopLegacyCustomManagedInstanceAsync(layout, cancellationToken);
            if (!await RunSystemdNginxAsync("disable", cancellationToken, "--now"))
                return new WebServerOperationResult("webserver.uninstall_failed");
        }
        else
            _ = await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, ["-s", "quit"]), cancellationToken);
        try
        {
            var uninstall = UsesSystemPackageManagedExecutable()
                ? await privilegedNginx.UninstallPackageAsync(cancellationToken)
                : new PrivilegedOperationResult(true);
            if (!uninstall.Success)
            {
                logger.LogWarning("Could not remove the APT-installed Nginx package for a managed installation. Executable={Executable}", layout.ExecutablePath);
                return new WebServerOperationResult(ToWebServerProblem(uninstall.ProblemCode, "webserver.uninstall_failed"));
            }
            if (isManaged) Directory.Delete(layout.Root, recursive: true);
            return new WebServerOperationResult("");
        }
        catch (UnauthorizedAccessException) { return new WebServerOperationResult("webserver.install_elevation_required"); }
        catch (IOException) { return new WebServerOperationResult("webserver.uninstall_failed"); }
    }

    private async Task<CommandResult> StartManagedAsync(ManagedLayout layout, CancellationToken cancellationToken)
    {
        if (IsManagedNginxRunning(layout)) return new CommandResult(true, "");
        var test = await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, ["-t"]), cancellationToken);
        if (!test.Success) return test;
        var start = await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, []), cancellationToken);
        if (!start.Success || await WaitForManagedNginxAsync(layout, cancellationToken)) return start;
        return new CommandResult(false, start.Output);
    }

    private async Task<CommandResult> RestartManagedAsync(ManagedLayout layout, CancellationToken cancellationToken)
    {
        _ = await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, ["-s", "quit"]), cancellationToken);
        return await StartManagedAsync(layout, cancellationToken);
    }

    private static bool CanUseBuiltInInstaller() => OperatingSystem.IsLinux() && File.Exists("/usr/bin/apt-get");

    internal static bool CanUninstallInstallation(bool managed, bool supportsApt, bool integrated,
        string executable, string? configuration) => managed ||
        (supportsApt && integrated && string.Equals(executable, "/usr/sbin/nginx", StringComparison.Ordinal)
            && string.Equals(configuration, "/etc/nginx/nginx.conf", StringComparison.Ordinal));

    // Arbitrary host-configured installers are deliberately not supported. Package installation
    // uses the fixed, Helper-owned apt operation; other platforms report not-supported.
    private Task<NginxInstallResult> RunInstallerAsync(ManagedLayout layout, string? version, CancellationToken cancellationToken) =>
        RunBuiltInLinuxInstallerAsync(layout, version, cancellationToken);

    private async Task<NginxInstallResult> RunBuiltInLinuxInstallerAsync(ManagedLayout layout, string? version, CancellationToken cancellationToken)
    {
        if (!CanUseBuiltInInstaller()) return new(false, PrivilegedProblemCode.UnsupportedOperation);
        // Check service-account storage before APT changes the host, so a missing marker
        // permission does not leave a successfully installed package reported as failed.
        try
        {
            if (IsSymbolicLink(layout.Root)) return new(false, PrivilegedProblemCode.AccessDenied);
            Directory.CreateDirectory(layout.Root);
            var probe = Path.Combine(layout.Root, $".write-probe-{Guid.NewGuid():N}");
            await using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1, FileOptions.DeleteOnClose))
                await stream.FlushAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "The service account cannot write the managed Nginx marker root. ManagedMarkerRoot={ManagedMarkerRoot}", layout.Root);
            return new(false, PrivilegedProblemCode.AccessDenied);
        }
        logger.LogInformation("Installing the APT Nginx package. RequestedVersion={RequestedVersion}, ManagedMarkerRoot={ManagedMarkerRoot}", version ?? "<default>", layout.Root);
        var package = await privilegedNginx.InstallPackageAsync(version, cancellationToken);
        if (!package.Success)
        {
            logger.LogWarning("APT Nginx package installation failed. ProblemCode={ProblemCode}, ExitCode={ExitCode}, Error={Error}", package.ProblemCode, package.ExitCode, package.Error);
            return new(false, package.ProblemCode);
        }
        if (!File.Exists(layout.ExecutablePath) || IsSymbolicLink(layout.ExecutablePath))
        {
            logger.LogWarning("APT reported successful Nginx installation but the executable cannot be used. Executable={Executable}", layout.ExecutablePath);
            return new(false, PrivilegedProblemCode.InternalError);
        }
        try
        {
            if (IsSymbolicLink(layout.Root))
            {
                logger.LogWarning("The managed Nginx marker root is a symbolic link. ManagedMarkerRoot={ManagedMarkerRoot}", layout.Root);
                return new(false, PrivilegedProblemCode.InternalError);
            }
            Directory.CreateDirectory(layout.Root);
            // The APT package owns both the executable and nginx.service. Keep that service
            // enabled and let systemd own the daemon lifecycle; RelaxKonOS manages only its
            // package ownership marker and the files it creates in /etc/nginx/conf.d.
            var systemd = await RunSystemdNginxOperationAsync("enable", cancellationToken, "--now");
            if (!systemd.Success)
                logger.LogWarning("Nginx package was installed but nginx.service could not be enabled and started. ProblemCode={ProblemCode}, ExitCode={ExitCode}, Error={Error}", systemd.ProblemCode, systemd.ExitCode, systemd.Error);
            return new(systemd.Success, systemd.ProblemCode);
        }
        catch (IOException exception)
        {
            logger.LogError(exception, "Could not prepare the managed Nginx marker root after package installation. ManagedMarkerRoot={ManagedMarkerRoot}", layout.Root);
            return new(false, PrivilegedProblemCode.InternalError);
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogError(exception, "Access was denied while preparing the managed Nginx marker root. ManagedMarkerRoot={ManagedMarkerRoot}", layout.Root);
            return new(false, PrivilegedProblemCode.AccessDenied);
        }
    }

    private async Task<bool> RunSystemdNginxAsync(string command, CancellationToken cancellationToken, params string[] additionalArguments)
        => (await RunSystemdNginxOperationAsync(command, cancellationToken, additionalArguments)).Success;

    private async Task<PrivilegedOperationResult> RunSystemdNginxOperationAsync(string command, CancellationToken cancellationToken, params string[] additionalArguments)
    {
        var action = (command, additionalArguments) switch
        {
            ("start", []) => NginxSystemServiceAction.Start,
            ("stop", []) => NginxSystemServiceAction.Stop,
            ("restart", []) => NginxSystemServiceAction.Restart,
            ("reload", []) => NginxSystemServiceAction.Reload,
            ("enable", []) => NginxSystemServiceAction.Enable,
            ("disable", []) => NginxSystemServiceAction.Disable,
            ("enable", ["--now"]) => NginxSystemServiceAction.EnableAndStart,
            ("disable", ["--now"]) => NginxSystemServiceAction.DisableAndStop,
            _ => throw new InvalidOperationException("Unsupported fixed Nginx systemd action."),
        };
        logger.LogInformation("Running nginx.service action. Action={Action}", action);
        var result = await privilegedNginx.ApplySystemServiceActionAsync(action, cancellationToken);
        if (result.Success)
            logger.LogInformation("nginx.service action completed. Action={Action}", action);
        else
            logger.LogWarning("nginx.service action failed. Action={Action}, ProblemCode={ProblemCode}, ExitCode={ExitCode}, Error={Error}", action, result.ProblemCode, result.ExitCode, result.Error);
        return result;
    }

    private async Task<bool> IsSystemdNginxActiveAsync(CancellationToken cancellationToken)
    {
        var status = await privilegedNginx.GetRuntimeStatusAsync(cancellationToken);
        if (!status.Success)
            logger.LogWarning("Nginx runtime status query failed through the privileged helper. ProblemCode={ProblemCode}, ExitCode={ExitCode}, Error={Error}",
                status.ProblemCode, status.ExitCode, status.Error);
        return status.Success && status.NginxRunning == true;
    }

    private async Task StopLegacyCustomManagedInstanceAsync(ManagedLayout layout, CancellationToken cancellationToken)
    {
        var legacyConfiguration = Path.Combine(layout.Root, "conf", "nginx.conf");
        if (!OperatingSystem.IsLinux() || !File.Exists(legacyConfiguration) || IsSymbolicLink(legacyConfiguration)) return;
        _ = await RunNginxAsync(layout.ExecutablePath, ["-p", layout.Root, "-c", legacyConfiguration, "-s", "quit"], cancellationToken);
    }

    private static async Task<bool> RunProcessAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true } };
            process.StartInfo.Environment["DEBIAN_FRONTEND"] = "noninteractive";
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch { return false; }
    }

    private ManagedLayout GetManagedLayout()
    {
        var configuredRoot = managedOptions.InstallationRoot.Trim();
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RelaxKonOS", "webserver", "nginx")
                : "/var/lib/relaxkonos/webserver/nginx"
            : Environment.ExpandEnvironmentVariables(configuredRoot);
        root = Path.GetFullPath(root);
        // APT owns the Linux executable at /usr/sbin/nginx.  The old implementation copied
        // it into the RelaxKonOS data directory after installing the package, which produced a
        // second Nginx installation and a second discovered instance.
        var executable = ResolveManagedExecutablePath(root, UsesSystemPackageManagedExecutable());
        var configuration = ResolveManagedConfigurationPath(root, UsesSystemPackageManagedService());
        return new ManagedLayout(root, executable, configuration, Path.Combine(root, ManagedMarkerName), InstanceId(executable));
    }

    private static bool UsesSystemPackageManagedExecutable() => OperatingSystem.IsLinux();

    private bool UsesSystemPackageManagedService() => UsesSystemPackageManagedExecutable();

    private bool UsesSystemPackageNginx(WebServerDto instance) => UsesSystemPackageManagedService()
        && string.Equals(Path.GetFullPath(instance.ExecutablePath), GetManagedLayout().ExecutablePath, StringComparison.Ordinal)
        && string.Equals(Path.GetFullPath(instance.ConfigurationPath ?? string.Empty), "/etc/nginx/nginx.conf", StringComparison.Ordinal);

    private static string ResolveManagedExecutablePath(string root, bool useSystemPackageExecutable) => OperatingSystem.IsWindows()
        ? Path.Combine(root, "nginx.exe")
        : useSystemPackageExecutable
            ? "/usr/sbin/nginx"
            : Path.Combine(root, "sbin", "nginx");

    private static string ResolveManagedConfigurationPath(string root, bool useSystemPackageService) => useSystemPackageService
        ? "/etc/nginx/nginx.conf"
        : Path.Combine(root, "conf", "nginx.conf");

    private static bool IsManagedInstallation(ManagedLayout layout)
    {
        try
        {
            return File.Exists(layout.ExecutablePath) && File.Exists(layout.MarkerPath)
                && !IsSymbolicLink(layout.Root) && !IsSymbolicLink(layout.ExecutablePath)
                && !IsSymbolicLink(layout.MarkerPath) && File.ReadAllText(layout.MarkerPath) == ManagedMarkerContent;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private IReadOnlyList<string> ManagedArguments(ManagedLayout layout, IReadOnlyList<string> operation) => UsesSystemPackageManagedService()
        ? operation
        : ["-p", layout.Root, "-c", layout.ConfigurationPath, .. operation];

    private static WebServerOperationDto Rejected(string instanceId, string kind, string problemCode) =>
        new(Guid.Empty, instanceId, kind, WebServerOperationState.Failed, "validation", problemCode, null, null, DateTimeOffset.UtcNow);

    private sealed record ManagedLayout(string Root, string ExecutablePath, string ConfigurationPath, string MarkerPath, string InstanceId);
    private sealed record NginxManagedInstallRequest(bool Confirmed, string? Version = null, InstallationFileSource? Source = null);
    private sealed record NginxInstallResult(bool Success, PrivilegedProblemCode ProblemCode);

    private static string ToWebServerProblem(PrivilegedProblemCode problemCode, string fallback) =>
        problemCode == PrivilegedProblemCode.HelperUnavailable ? "webserver.privileged_helper_unavailable" : fallback;

    private static string ToNginxConfigurationProblem(PrivilegedOperationResult result) => result.ProblemCode switch
    {
        PrivilegedProblemCode.HelperUnavailable => "webserver.privileged_helper_unavailable",
        PrivilegedProblemCode.AccessDenied => "webserver.config_elevation_required",
        PrivilegedProblemCode.Conflict or PrivilegedProblemCode.NotFound => "webserver.configuration_changed",
        _ => "webserver.config_elevation_required"
    };

    internal sealed class WebServerSiteValidationException(string problemCode) : Exception(problemCode)
    {
        public string ProblemCode { get; } = problemCode;
    }

    internal sealed class WebServerSiteConflictException(string problemCode) : Exception(problemCode)
    {
        public string ProblemCode { get; } = problemCode;
    }

    internal sealed class WebServerSiteApplyException(string problemCode) : Exception(problemCode)
    {
        public string ProblemCode { get; } = problemCode;
    }

}
