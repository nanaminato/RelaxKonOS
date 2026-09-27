using System.Runtime.InteropServices;
using System.Text.Json;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;

/// <summary>
/// Fixed host-process execution and protocol framing shared by the closed operation handlers.
/// No caller-controlled executable or command line reaches this boundary.
/// </summary>
public static partial class PrivilegedOperationExecutor
{
    static Task<PrivilegedOperationResult> RunUfwAsync(IReadOnlyList<string> arguments, string failure) => !OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/ufw")
        ? Task.FromResult(Fail(64, PrivilegedProblemCode.UnsupportedOperation, "ufw is unavailable"))
        : RunFixedCommandAsync("/usr/sbin/ufw", arguments, TimeSpan.FromSeconds(30), failure);

    static async Task<PrivilegedOperationResult> RunAptAsync(IReadOnlyList<string> arguments, TimeSpan timeout, string failure)
    {
        var stage = arguments[0] == "update" ? InstallationStage.UpdatingPackageLists : InstallationStage.Installing;
        if (Progress.Value is { } initial) await initial(PrivilegedOperationFrame.Report(stage));
        using var statusPipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.In, HandleInheritability.Inheritable);
        using var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo("/usr/bin/apt-get")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add("APT::Status-Fd=" + statusPipe.GetClientHandleAsString());
        process.StartInfo.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) return Fail(69, PrivilegedProblemCode.HelperUnavailable, "package operation could not start");
        statusPipe.DisposeLocalCopyOfClientHandle();
        var output = DrainAsync(process.StandardOutput.BaseStream);
        var error = DrainAsync(process.StandardError.BaseStream);
        var status = ReadAptStatusAsync(statusPipe, stage);
        using var deadline = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error, status);
            return Fail(124, PrivilegedProblemCode.TimedOut, "package operation timed out");
        }
        await Task.WhenAll(output, error, status);
        return process.ExitCode == 0 ? new(true) : Fail(process.ExitCode, PrivilegedProblemCode.InternalError, "package operation failed");
    }

    static async Task DrainAsync(Stream stream)
    {
        var buffer = new byte[8192];
        while (await stream.ReadAsync(buffer) > 0) { }
    }

    static async Task ReadAptStatusAsync(Stream stream, InstallationStage stage)
    {
        var buffer = new byte[4096]; var line = new System.Text.StringBuilder(); var oversized = false; int read; int? previous = null;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var value = (char)buffer[i];
                if (value != '\n') { if (line.Length >= 4096) oversized = true; if (!oversized) line.Append(value); continue; }
                if (!oversized)
                {
                    var fields = line.ToString().Split(':', 4);
                    if (fields.Length >= 3 && fields[0] is "pmstatus" or "dlstatus"
                        && double.TryParse(fields[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent)
                        && double.IsFinite(percent) && percent is >= 0 and <= 100)
                    {
                        var current = (int)percent;
                        if (current != previous && Progress.Value is { } observer)
                        {
                            await observer(PrivilegedOperationFrame.Report(stage, current));
                            previous = current;
                        }
                    }
                }
                line.Clear(); oversized = false;
            }
        }
    }

    static async Task<PrivilegedOperationResult> RunFixedCommandWithOutputAsync(string executable, IReadOnlyList<string> arguments, string failure)
    {
        using var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        TrustedProcessEnvironment.Apply(process.StartInfo);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) return Fail(69, PrivilegedProblemCode.HelperUnavailable, "host operation could not start");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(cancellation.Token);
        var error = process.StandardError.ReadToEndAsync(cancellation.Token);
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch (OperationCanceledException) { return Fail(124, PrivilegedProblemCode.TimedOut, "host operation timed out"); }
        var text = (await output) + (await error);
        var encodedOutput = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(BoundCommandOutput(text)));
        return process.ExitCode == 0
            ? new(true, OutputBase64: encodedOutput)
            : new(false, process.ExitCode, OutputBase64: encodedOutput, Error: failure, ProblemCode: PrivilegedProblemCode.InternalError);
    }

    static string BoundCommandOutput(string text)
    {
        const int maximumLength = 8_192;
        var trimmed = text.Trim();
        return trimmed.Length <= maximumLength ? trimmed : trimmed[..maximumLength] + "…";
    }

    static async Task<PrivilegedOperationResult> RunFixedCommandAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, string failure, string? diagnostic = null)
    {
        using var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        TrustedProcessEnvironment.Apply(process.StartInfo);
        process.StartInfo.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) return Fail(69, PrivilegedProblemCode.HelperUnavailable, "host operation could not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch (OperationCanceledException) { return Fail(124, PrivilegedProblemCode.TimedOut, "host operation timed out"); }
        await Task.WhenAll(output, error);
        if (process.ExitCode == 0) return new(true);
        if (diagnostic is not null) WriteHelperDiagnostic(diagnostic, process.ExitCode);
        return Fail(1, PrivilegedProblemCode.InternalError, failure);
    }

    static void WriteHelperDiagnostic(string eventName, int exitCode) => Console.Error.WriteLine($"relaxkonos-diagnostic:{eventName} exit={exitCode}");
    static PrivilegedOperationResult Fail(int exitCode, PrivilegedProblemCode code, string error) => new(false, exitCode, Error: error, ProblemCode: code);
    static Task WriteResultAsync(PrivilegedOperationResult result) => Console.Out.WriteLineAsync(JsonSerializer.Serialize(PrivilegedOperationFrame.Completed(result)));

    static async Task<PrivilegedOperationRequest?> ReadRequestAsync(Stream input)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(chunk);
                if (read == 0) break;
                if (buffer.Length + read > PrivilegedOperationProtocol.MaximumRequestBytes)
                    throw new InvalidDataException("request too large");
                await buffer.WriteAsync(chunk.AsMemory(0, read));
            }
            buffer.Position = 0;
            return await JsonSerializer.DeserializeAsync<PrivilegedOperationRequest>(buffer);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(chunk);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
        }
    }

    static StringComparison GetPathComparison() => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    static StringComparer GetPathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [DllImport("libc")]
    static extern uint geteuid();
}
