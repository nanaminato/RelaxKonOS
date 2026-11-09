using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Terminal;

namespace RelaxKonOS.Server.Proxy.Mihomo;

internal static class MihomoVersionWorker
{
    internal static async Task<int> RunAsync()
    {
        var request = await Console.In.ReadLineAsync();
        if (request is null) return 0;
        var path = JsonSerializer.Deserialize<string>(request);
        if (path is null || !MihomoRuntimeManager.TryResolveExternalExecutable(path, out var executable)) return 0;

        // The Windows parent assigns this worker to its Job before sending the request.
        var tree = OperatingSystem.IsLinux() ? new LinuxTerminalProcessTree() : null;
        if (tree is null && !OperatingSystem.IsWindows()) return 0;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        TrustedProcessEnvironment.Apply(process.StartInfo);
        process.StartInfo.ArgumentList.Add("-v");
        try { if (!process.Start()) return 0; }
        catch (Win32Exception) { return 0; }
        process.StandardInput.Close();
        var output = ReadVersionAsync(process.StandardOutput);
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        var exit = process.WaitForExitAsync();
        var stop = Task.Run(Console.In.Read);
        var completed = await Task.WhenAny(exit, stop) == exit;
        if (tree is not null && !completed && !process.HasExited)
        {
            try
            {
                using var root = LinuxTerminalProcessExit.Capture(process.Id);
                root.Terminate();
            }
            catch (IOException) when (process.HasExited) { }
        }
        var succeeded = completed && process.ExitCode == 0;
        if (tree is not null)
        {
            await exit;
            await tree.DrainAsync();
        }
        // On Windows the owning parent can terminate the Job if any member keeps the pipes open.
        await Task.WhenAll(exit, output, errors);
        if (succeeded) await Console.Out.WriteAsync(await output);
        return 0;
    }

    private static async Task<string> ReadVersionAsync(StreamReader reader)
    {
        var version = new StringBuilder(128);
        var buffer = new char[512];
        int read;
        while ((read = await reader.ReadAsync(buffer)) != 0)
            version.Append(buffer, 0, Math.Min(read, 128 - version.Length));
        return version.ToString().Trim();
    }
}
