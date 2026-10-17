using System.Text.Json;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Server.Proxy;

namespace RelaxKonOS.Server.GatewayProxy;

/// <summary>Host-wide editor state. Publication is not proof of runtime adoption.</summary>
public sealed class SquidConfigurationStore(IProxyPlatformPaths paths)
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    private string FilePath => Path.Combine(paths.GetStateDirectory(), "squid-configuration.json");

    internal async Task<SquidConfigurationState> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(FilePath)) return new(null, 0, null, null, null);
        await using var stream = File.OpenRead(FilePath);
        return await JsonSerializer.DeserializeAsync<SquidConfigurationState>(stream, cancellationToken: ct)
            ?? throw new InvalidDataException("Invalid Squid configuration state.");
    }

    internal async Task WriteAsync(SquidConfigurationState state, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(stream, state, cancellationToken: ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, true);
        }
        finally { File.Delete(temporary); }
    }
}
