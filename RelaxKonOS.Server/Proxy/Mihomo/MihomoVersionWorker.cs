using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Terminal;

namespace RelaxKonOS.Server.Proxy.Mihomo;

internal static class MihomoVersionWorker
{
    internal const int NoDispatch = 65;
    internal sealed record Request(Guid OperationId, string Executable);
    internal sealed record Result(Guid OperationId, string? Version);
    internal static readonly JsonSerializerOptions WireJson = new()
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal static async Task<int> RunAsync()
    {
        var line = await Console.In.ReadLineAsync();
        if (line is null) return NoDispatch;
        var request = JsonSerializer.Deserialize<Request>(line, WireJson);
        if (request is null || request.OperationId == Guid.Empty
            || !MihomoRuntimeManager.TryResolveExternalExecutable(request.Executable, out var executable))
            return NoDispatch;

        // The Windows parent assigns this worker to its Job before sending the request.
        var tree = OperatingSystem.IsLinux() ? new LinuxTerminalProcessTree() : null;
        if (tree is null && !OperatingSystem.IsWindows()) return NoDispatch;
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
        try { if (!process.Start()) return NoDispatch; }
        catch (Win32Exception) { return NoDispatch; }
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
        if (completed)
        {
            try { await Console.Out.WriteAsync(JsonSerializer.Serialize(new Result(request.OperationId, succeeded ? await output : null))); }
            catch (IOException) { /* The original Server can exit after the probe has drained. */ }
        }
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
