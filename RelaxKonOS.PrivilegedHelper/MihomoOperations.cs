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
        var output = await NativeRuntimeProcess.RunAsync(binary, ["-t", "-d", dataDirectory, "-f", configuration], 65536);
        if (!output.Contains("test is successful", StringComparison.Ordinal))
            throw new RuntimePolicyException("runtime.native_configuration_invalid");
    }
}
