using System.Text.Json;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.UserExecution;

/// <summary>Bounded newline-delimited extraction snapshots followed by one terminal result.</summary>
public static class UserExecutionArchiveStream
{
    public static async Task<UserExecutionResult> ReadAsync(Stream stream, Action<ArchiveExtractionProgress>? progress, CancellationToken ct)
    {
        using var frame = new MemoryStream();
        var buffer = new byte[16384];
        long received = 0;
        UserExecutionResult? result = null;
        void Consume()
        {
            if (frame.Length == 0) return;
            var value = JsonSerializer.Deserialize<UserExecutionResult>(frame.GetBuffer().AsSpan(0, (int)frame.Length), RelaxKonOSJsonOptions.Default)
                ?? throw new InvalidDataException("Missing extraction result.");
            frame.SetLength(0);
            if (value.Version != UserExecutionProtocol.Version || result is not null) throw new InvalidDataException("Invalid extraction frame sequence.");
            if (value.ArchiveProgress is { } snapshot)
            {
                if (!value.Success || value.OutputBase64 is not null) throw new InvalidDataException("Invalid extraction snapshot.");
                progress?.Invoke(snapshot);
            }
            else result = value;
        }
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            received += read;
            if (received > UserExecutionProtocol.MaximumResponseBytes) throw new InvalidDataException("Extraction response is too large.");
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n') Consume();
                else
                {
                    if (frame.Length >= 65536) throw new InvalidDataException("Extraction frame is too large.");
                    frame.WriteByte(buffer[i]);
                }
            }
        }
        Consume();
        return result ?? throw new InvalidDataException("Extraction worker returned no terminal result.");
    }
}
