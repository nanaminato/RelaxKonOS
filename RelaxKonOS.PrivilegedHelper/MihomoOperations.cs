using RelaxKonOS.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Fixed native operations for a managed Mihomo binary. Both invocations are closed: no caller-supplied
/// executable, argument list or configuration path reaches this boundary.
/// </summary>
internal static class MihomoOperations
{
    /// <summary>Reads the single 'mihomo -v' report used as build evidence.</summary>
    internal static Task<string> VersionAsync(string binary) => NativeRuntimeProcess.RunAsync(binary, ["-v"], 8192);

    /// <summary>
    /// Runs the native configuration test and requires its own success sentence. A zero exit code alone
    /// is not treated as acceptance, and the bounded log never crosses the privilege boundary.
    /// </summary>
    internal static async Task TestConfigurationAsync(string binary, string dataDirectory, string configuration)
    {
        string output;
        if (OperatingSystem.IsLinux())
        {
            // A profile is not permission for a root native checker to write arbitrary host files or
            // download provider/GEO data outside the controlled preparation path. Use the same fixed
            // systemd boundary as the service, with no network or device privileges for a check.
            if (!Directory.Exists("/run/systemd/system")) throw new RuntimePolicyException("runtime.service_host_unavailable");
            if (new[] { binary, dataDirectory, configuration }.Any(path => !Path.IsPathFullyQualified(path) || path.Any(char.IsWhiteSpace)))
                throw new RuntimePolicyException("runtime.service_scope_invalid");
            output = await NativeRuntimeProcess.RunAsync("/usr/bin/systemd-run",
                ["--quiet", "--pipe", "--wait", "--collect", "--service-type=exec",
                 "--property=RuntimeMaxSec=40s", "--property=TimeoutStopSec=2s", "--property=User=root",
                 "--property=NoNewPrivileges=yes", "--property=ProtectSystem=strict", "--property=ProtectHome=yes",
                 "--property=InaccessiblePaths=-/etc/relaxkonos -/opt/relaxkonos",
                 "--property=ProtectKernelTunables=yes", "--property=ProtectControlGroups=yes",
                 "--property=PrivateDevices=yes", "--property=PrivateNetwork=yes", "--property=PrivateTmp=yes",
                 "--property=CapabilityBoundingSet=", "--property=RestrictAddressFamilies=AF_UNIX",
                 "--property=BindPaths=" + dataDirectory,
                 "--property=BindReadOnlyPaths=" + binary + " " + configuration,
                 "--property=ReadWritePaths=" + dataDirectory, "--", binary, "-t", "-d", dataDirectory, "-f", configuration], 65536);
        }
        else output = await NativeRuntimeProcess.RunAsync(binary, ["-t", "-d", dataDirectory, "-f", configuration], 65536);
        if (!output.Contains("test is successful", StringComparison.Ordinal))
            throw new RuntimePolicyException("runtime.native_configuration_invalid");
    }
}
