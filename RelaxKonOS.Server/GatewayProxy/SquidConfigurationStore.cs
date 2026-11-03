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
        RelaxKonOS.ManagedRuntimes.RuntimeSourceStore.RequireNoLinks(FilePath);
        try
        {
            await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length is 0 or > 2 * 1024 * 1024) throw new InvalidDataException("Invalid Squid configuration state size.");
            var state = await JsonSerializer.DeserializeAsync<SquidConfigurationState>(stream,
                new JsonSerializerOptions { RespectRequiredConstructorParameters = true,
                    UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow }, ct)
                ?? throw new InvalidDataException("Invalid Squid configuration state.");
            if (state.DesiredRevision < 0 || state.PublishedRevision is { } revision && (revision < 0 || revision > state.DesiredRevision))
                throw new InvalidDataException("Invalid Squid configuration revision.");
            return state;
        }
        catch (FileNotFoundException) { return new(null, 0, null, null, null); }
        catch (DirectoryNotFoundException) { return new(null, 0, null, null, null); }
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
