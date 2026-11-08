using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

internal enum MihomoTunPhase { Intent, NetworkDispatched, LaunchDispatched, DescriptorDispatched, StopDispatched, Restored }

internal sealed record MihomoTunIntent(Guid OperationId, Guid RevisionId, string ConfigurationHash,
    Guid BootId, string NetworkNamespace, string Device, string Address, string Destination, string ManagementAddress, string ManagementHash);

internal sealed record MihomoTunProcess(int Pid, Guid InvocationId, ulong StartTicks);

internal sealed record MihomoTunEffect(MihomoTunPhase Phase, MihomoTunIntent Intent, uint InterfaceIndex = 0,
    int BrokerPid = 0, MihomoTunProcess? Process = null);

/// <summary>Append-only network evidence under the original archive operation. The writer must hold
/// the archive instance lock. A partial checkpoint blocks further writes and cannot authorize replay.</summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxMihomoTunCheckpoint(string root, Guid operationId, Guid revisionId, string configurationHash)
{
    private readonly string directory = Path.Combine(root, "operations", operationId.ToString("N"));
    internal string SocketPath => Path.Combine(directory, "tun.sock");

    internal static Guid BootId() => Guid.Parse(File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim());

    internal static string NetworkNamespace() => new FileInfo("/proc/self/ns/net").LinkTarget
        ?? throw new RuntimePolicyException("runtime.network_namespace_unknown");

    private string PathFor(MihomoTunPhase phase) => Path.Combine(directory, $"tun-{(int)phase}.json");

    internal MihomoTunEffect? Read()
    {
        MihomoTunEffect? previous = null;
        foreach (var phase in Enum.GetValues<MihomoTunPhase>())
        {
            var path = PathFor(phase);
            RuntimeSourceStore.RequireNoLinks(path);
            if (Directory.Exists(path)) throw Conflict();
            if (!File.Exists(path)) continue;
            RuntimeSourceProtection.RequireRootOwned(directory);
            RuntimeSourceProtection.RequireRootOwned(path);
            if (File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite)
                || new FileInfo(path).Length is <= 0 or > 4096)
                throw Conflict();
            MihomoTunEffect entry;
            try { entry = JsonSerializer.Deserialize<MihomoTunEffect>(File.ReadAllBytes(path)) ?? throw Conflict(); }
            catch (JsonException) { throw Conflict(); }
            if (entry.Phase != phase) throw Conflict();
            Validate(previous, entry);
            previous = entry;
        }
        return previous;
    }

    internal async Task AppendAsync(MihomoTunEffect effect)
    {
        RuntimeSourceProtection.RequireRootOwned(directory);
        Validate(Read(), effect);
        var path = PathFor(effect.Phase);
        await using (var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        }))
        {
            await file.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(effect));
            file.Flush(flushToDisk: true);
        }
        LinuxDirectorySync.Flush(directory);
        // The archive operation directory may have been created in the same transaction.
        LinuxDirectorySync.Flush(Path.GetDirectoryName(directory)!);
    }

    private void Validate(MihomoTunEffect? previous, MihomoTunEffect current)
    {
        var intent = current.Intent;
        if ((int)current.Phase != (previous is null ? 0 : (int)previous.Phase + 1)
            || intent is null || intent.OperationId == Guid.Empty || intent.OperationId != operationId
            || intent.RevisionId == Guid.Empty || intent.RevisionId != revisionId
            || intent.ConfigurationHash is not { Length: 64 } || !intent.ConfigurationHash.All(Uri.IsHexDigit)
            || !string.Equals(intent.ConfigurationHash, configurationHash, StringComparison.OrdinalIgnoreCase)
            || intent.ManagementHash is not { Length: 64 } || !intent.ManagementHash.All(Uri.IsHexDigit)
            || intent.BootId == Guid.Empty || string.IsNullOrEmpty(intent.NetworkNamespace)
            || string.IsNullOrEmpty(intent.Device) || !LinuxMihomoTunDescriptor.ValidName(intent.Device)
            || !IPAddress.TryParse(intent.Address, out var address) || !IPAddress.TryParse(intent.Destination, out var destination)
            || !IPAddress.TryParse(intent.ManagementAddress, out var management)
            || (current.InterfaceIndex == 0) != (current.Phase == MihomoTunPhase.Intent)
            || (current.Phase == MihomoTunPhase.Intent ? current.BrokerPid != 0 : current.BrokerPid <= 1)
            || (current.Process is null) != (current.Phase < MihomoTunPhase.DescriptorDispatched)
            || current.Process is { } process && (process.Pid <= 1 || process.InvocationId == Guid.Empty || process.StartTicks == 0)
            || previous is not null && (previous.Intent != intent
                || previous.InterfaceIndex != 0 && previous.InterfaceIndex != current.InterfaceIndex
                || previous.BrokerPid != 0 && previous.BrokerPid != current.BrokerPid
                || previous.Process is not null && previous.Process != current.Process))
            throw Conflict();
        _ = new LinuxMihomoTunRoutes(address, destination, management);
    }

    private static RuntimePolicyException Conflict() => new("runtime.network_checkpoint_conflict");
}
