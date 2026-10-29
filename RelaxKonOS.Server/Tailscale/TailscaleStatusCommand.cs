using System.Diagnostics;

namespace RelaxKonOS.Server.Tailscale;

public sealed record TailscaleCommandResult(string State,
    [property: System.Text.Json.Serialization.JsonIgnore] string? Json = null)
{
    public override string ToString() => "TailscaleCommandResult { State = " + State + " }";
}
internal sealed record TailscaleCapturedResult(string State, int? ExitCode = null, [property: System.Text.Json.Serialization.JsonIgnore] string? Output = null)
{
    public override string ToString() => $"TailscaleCapturedResult {{ State = {State}, ExitCode = {ExitCode} }}";
}

/// <summary>Only observes status; callers cannot supply executable paths or arguments.</summary>
public interface ITailscaleStatusCommand
{
    Task<TailscaleCommandResult> ReadAsync(CancellationToken cancellationToken);
}

public sealed class TailscaleStatusCommand : ITailscaleStatusCommand
{
    public Task<TailscaleCommandResult> ReadAsync(CancellationToken cancellationToken)
        => RunFixedAsync(["status", "--json"], cancellationToken);

    internal static async Task<TailscaleCommandResult> RunFixedAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var captured = await CaptureFixedAsync(arguments, cancellationToken);
        return captured.State == "exited" ? captured.ExitCode == 0 ? new("observed", captured.Output) : new("unavailable")
            : new(captured.State);
    }

    // Login is the only caller that interprets structured stdout after a nonzero exit. Stderr is always discarded.
    internal static async Task<TailscaleCapturedResult> CaptureFixedAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return new("unsupported");
        var path = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe")
            : "/usr/bin/tailscale";
        if (!File.Exists(path)) return new("not-installed");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = new Process { StartInfo = new(path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        }};
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment.Clear();
        process.StartInfo.Environment["PATH"] = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32") : "/usr/sbin:/usr/bin:/sbin:/bin";
        if (OperatingSystem.IsWindows()) process.StartInfo.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        try
        {
            process.Start();
            var output = ReadBoundedAsync(process.StandardOutput.BaseStream, timeout);
            var error = ReadBoundedAsync(process.StandardError.BaseStream, timeout);
            try { await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)); }
            catch when (output.Exception?.GetBaseException() is IOException || error.Exception?.GetBaseException() is IOException)
            { throw new IOException("Tailscale output could not be read within its limits."); }
            return new("exited", process.ExitCode, System.Text.Encoding.UTF8.GetString(await output));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new("timeout"); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        { return new("unavailable"); }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationTokenSource lifetime)
    {
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer, lifetime.Token)) != 0)
            {
                if (result.Length + count > 1024 * 1024) throw new IOException("Tailscale response exceeds limit.");
                result.Write(buffer, 0, count);
            }
            return result.ToArray();
        }
        catch
        {
            // Release the other reader and process wait immediately if either stream fails.
            lifetime.Cancel();
            throw;
        }
    }
}
