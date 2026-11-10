using System.Text.Json;
using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>The shared official-archive pointer and its single serialization gate.</summary>
public sealed class MihomoOfficialRuntimeState(IProxyPlatformPaths paths)
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    internal async Task<RuntimeState?> ReadAsync(CancellationToken cancellationToken)
    {
        var path = PointerPath; if (!File.Exists(path)) return null;
        try { await using var input = File.OpenRead(path); return await JsonSerializer.DeserializeAsync<RuntimeState>(input, cancellationToken: cancellationToken); }
        catch (JsonException) { return null; }
    }

    internal string PointerPath => Path.Combine(paths.GetStateDirectory(), "mihomo-runtime.json");
    internal string VersionDirectory(string version) => Path.Combine(paths.GetEngineVersionsDirectory(MihomoEngine.Id), ReleaseDirectoryId(version));
    internal string ExecutablePath(string version) => Path.Combine(VersionDirectory(version), BinaryName());
    internal static string ReleaseDirectoryId(string version) => version + "-" + MihomoRuntimeManifest.CurrentRid();
    internal static string BinaryName() => OperatingSystem.IsWindows() ? "mihomo.exe" : "mihomo";

    internal sealed record RuntimeState(string ActiveVersion, string? PreviousVersion, DateTimeOffset ActivatedAt);
}
