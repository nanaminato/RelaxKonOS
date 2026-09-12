using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Installations;

namespace RelaxKonOS.Server.Privileged;

internal static class PrivilegedFrameReader
{
    public static async Task<PrivilegedOperationResult> ReadAsync(Stream stream)
    {
        PrivilegedOperationResult? result = null;
        var count = 0;
        var invalid = false;
        using var line = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    if (line.Length >= PrivilegedOperationProtocol.MaximumRequestBytes) invalid = true;
                    if (!invalid) line.WriteByte(buffer[i]);
                    continue;
                }
                if (++count > PrivilegedOperationFrame.MaximumFrames) invalid = true;
                if (!invalid)
                {
                    try
                    {
                        var frame = JsonSerializer.Deserialize<PrivilegedOperationFrame>(line.ToArray());
                        if (result is not null || frame is null || !frame.IsValid()
                            || frame.Type == "progress" && line.Length > PrivilegedOperationFrame.MaximumProgressFrameBytes) invalid = true;
                        else if (frame.Result is { } terminal) result = terminal;
                        else await ReportAsync(frame);
                    }
                    catch { invalid = true; }
                }
                line.SetLength(0);
            }
        }
        return !invalid && line.Length == 0 && result is not null ? result
            : new(false, 65, ProblemCode: PrivilegedProblemCode.InvalidProtocol);
    }

    public static async Task ReportAsync(PrivilegedOperationFrame frame)
    {
        if (InstallationExecutionContext.Progress.Value is { } observer && frame.Stage is { } stage)
            await observer.ReportAsync(new(stage, frame.Progress));
    }
    public static async Task<string> DrainAsync(Stream stream)
    {
        const int maximumDiagnosticBytes = 4096;
        await using var captured = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer)) != 0)
        {
            var count = Math.Min(read, maximumDiagnosticBytes - (int)captured.Length);
            if (count > 0) await captured.WriteAsync(buffer.AsMemory(0, count));
        }
        return Encoding.UTF8.GetString(captured.ToArray());
    }
}
