using System.Diagnostics;
using System.Text;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Fixed host-process invocation for an installed runtime binary. The executable, arguments and working
/// directory are always derived by the caller; output is bounded instead of truncated so an oversized
/// report is a refusal rather than a silently shortened one.
/// </summary>
internal static class NativeRuntimeProcess
{
    internal static async Task<string> RunAsync(string binary, IReadOnlyList<string> arguments,
        int maximumOutputCharacters, int? allowedExitCode = null)
    {
        using var process = new Process
        {
            StartInfo = new(binary)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        TrustedProcessEnvironment.Apply(process.StartInfo);
        process.StartInfo.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(binary))!;
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        // A runtime binary must never inherit the caller's outbound proxy for a local check.
        foreach (var variable in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
            process.StartInfo.Environment.Remove(variable);
        if (!process.Start()) throw new IOException();
        var output = ReadBoundedAsync(process.StandardOutput, maximumOutputCharacters);
        var errors = ReadBoundedAsync(process.StandardError, maximumOutputCharacters);
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
        catch { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        await Task.WhenAll(output, errors);
        // Native diagnostics can contain secrets: never cross the privilege boundary.
        if (process.ExitCode != 0 && process.ExitCode != allowedExitCode) throw new InvalidOperationException("runtime operation failed.");
        return await output;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumOutputCharacters)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; int read;
        var oversized = false;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            if (result.Length + read > maximumOutputCharacters) oversized = true;
            if (result.Length < maximumOutputCharacters) result.Append(buffer, 0, Math.Min(read, maximumOutputCharacters - result.Length));
        }
        if (oversized) throw new IOException("runtime output exceeds limit.");
        return result.ToString();
    }
}
