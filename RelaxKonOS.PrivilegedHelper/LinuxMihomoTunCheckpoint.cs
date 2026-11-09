using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

internal enum MihomoTunPhase { Intent, NetworkDispatched, LaunchDispatched, DescriptorDispatched, DnsDispatched, DnsApplied, StopDispatched, RuleRemovalDispatched, Restored }

internal sealed record MihomoTunIntent(Guid OperationId, Guid RevisionId, string ConfigurationHash,
    Guid BootId, string NetworkNamespace, ulong NetworkNamespaceCookie, string Device, string Address, string Destination, string ManagementAddress,
    uint ManagementUserId, string ManagementHash, uint RoutingTable, string? DnsRoutingDomain);

internal sealed record MihomoTunProcess(int Pid, Guid InvocationId, ulong StartTicks);

internal sealed record MihomoTunEffect(MihomoTunPhase Phase, MihomoTunIntent Intent, uint InterfaceIndex = 0,
    int BrokerPid = 0, MihomoTunProcess? Process = null, MihomoTunDnsBaseline? Dns = null);

/// <summary>Append-only network evidence under the original archive operation. The writer must hold
/// the archive instance lock. A partial checkpoint blocks further writes and cannot authorize replay.</summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxMihomoTunCheckpoint(string root, Guid operationId, Guid revisionId, string configurationHash)
{
    private const int SolSocket = 1;
    private const int SoNetnsCookie = 71;
    private static readonly JsonSerializerOptions CheckpointJson = new()
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string directory = Path.Combine(root, "operations", operationId.ToString("N"));
    internal string SocketPath => Path.Combine(directory, "tun.sock");

    internal static Guid BootId() => Guid.Parse(File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim());

    internal static string NetworkNamespace() => new FileInfo("/proc/self/ns/net").LinkTarget
        ?? throw new RuntimePolicyException("runtime.network_namespace_unknown");

    internal static ulong NetworkNamespaceCookie()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        uint length = sizeof(ulong);
        if (getsockopt(socket.SafeHandle, SolSocket, SoNetnsCookie, out var cookie, ref length) != 0
            || length != sizeof(ulong) || cookie == 0)
            throw new RuntimePolicyException("runtime.network_namespace_unknown");
        return cookie;
    }

    internal static bool MatchesCurrentNetwork(MihomoTunIntent intent) =>
        intent.BootId == BootId() && intent.NetworkNamespace == NetworkNamespace()
        && intent.NetworkNamespaceCookie == NetworkNamespaceCookie();

    [DllImport("libc")]
    private static extern int getsockopt(SafeSocketHandle socket, int level, int option, out ulong value, ref uint length);

    private string PathFor(MihomoTunPhase phase) => Path.Combine(directory, $"tun-{(int)phase}.json");
    private string OwnerPath => Path.Combine(directory, "network-owner.json");
    private string InitialStopPath => Path.Combine(directory, "initial-service-stop.json");

    internal Task WriteOwnerAsync(MihomoTunEffect owner)
    {
        Validate(null, owner);
        return WriteAsync(OwnerPath, owner);
    }

    internal MihomoTunEffect ReadOwner()
    {
        var owner = ReadEntry(OwnerPath) ?? throw Conflict();
        Validate(null, owner);
        if (!MatchesCurrentNetwork(owner.Intent)) throw Conflict();
        return owner;
    }

    internal Task MarkInitialStopAsync(MihomoTunEffect owner) => WriteAsync(InitialStopPath, owner);

    internal bool InitialStopDispatched(MihomoTunEffect owner)
    {
        var dispatched = ReadEntry(InitialStopPath);
        if (dispatched is null) return false;
        if (dispatched != owner) throw Conflict();
        return true;
    }

    internal MihomoTunEffect? Read()
    {
        MihomoTunEffect? previous = null;
        foreach (var phase in Enum.GetValues<MihomoTunPhase>())
        {
            if (ReadEntry(PathFor(phase)) is not { } entry) continue;
            if (entry.Phase != phase) throw Conflict();
            Validate(previous, entry);
            previous = entry;
        }
        return previous;
    }

    private MihomoTunEffect? ReadEntry(string path)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        if (Directory.Exists(path)) throw Conflict();
        if (!File.Exists(path)) return null;
        RuntimeSourceProtection.RequireRootOwned(directory);
        RuntimeSourceProtection.RequireRootOwned(path);
        if (File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite)
            || new FileInfo(path).Length is <= 0 or > 4096)
            throw Conflict();
        try { return JsonSerializer.Deserialize<MihomoTunEffect>(File.ReadAllBytes(path), CheckpointJson) ?? throw Conflict(); }
        catch (JsonException) { throw Conflict(); }
    }

    internal Task AppendAsync(MihomoTunEffect effect)
    {
        Validate(Read(), effect);
        return WriteAsync(PathFor(effect.Phase), effect);
    }

    private async Task WriteAsync(string path, MihomoTunEffect effect)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        RuntimeSourceProtection.RequireRootOwned(directory);
        await using (var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        }))
        {
            await file.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(effect, CheckpointJson));
            file.Flush(flushToDisk: true);
        }
        LinuxDirectorySync.Flush(directory);
        // The archive operation directory may have been created in the same transaction.
        LinuxDirectorySync.Flush(Path.GetDirectoryName(directory)!);
    }

    private void Validate(MihomoTunEffect? previous, MihomoTunEffect current)
    {
        var intent = current.Intent;
        var next = previous?.Phase switch
        {
            null => MihomoTunPhase.Intent,
            MihomoTunPhase.DescriptorDispatched when previous.Intent.DnsRoutingDomain is null
                || current.Phase == MihomoTunPhase.StopDispatched => MihomoTunPhase.StopDispatched,
            MihomoTunPhase.DnsDispatched when current.Phase == MihomoTunPhase.StopDispatched => MihomoTunPhase.StopDispatched,
            _ => (MihomoTunPhase)((int)previous.Phase + 1)
        };
        if (current.Phase != next
            || intent is null || intent.OperationId == Guid.Empty || intent.OperationId != operationId
            || intent.RevisionId == Guid.Empty || intent.RevisionId != revisionId
            || intent.ConfigurationHash is not { Length: 64 } || !intent.ConfigurationHash.All(Uri.IsHexDigit)
            || !string.Equals(intent.ConfigurationHash, configurationHash, StringComparison.OrdinalIgnoreCase)
            || intent.ManagementHash is not { Length: 64 } || !intent.ManagementHash.All(Uri.IsHexDigit)
            || intent.RoutingTable != LinuxMihomoTunRoutes.TableFor(operationId)
            || intent.DnsRoutingDomain is { } domain && !MihomoRuntimeMaterial.ValidDnsRoutingDomain(domain)
            || (current.Dns is not null) != (intent.DnsRoutingDomain is not null && current.Phase >= MihomoTunPhase.DnsDispatched)
            || current.Dns is { } dns && !LinuxMihomoTunDns.ValidBaseline(dns)
            || intent.BootId == Guid.Empty || string.IsNullOrEmpty(intent.NetworkNamespace) || intent.NetworkNamespaceCookie == 0
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
                || previous.Process is not null && previous.Process != current.Process
                || previous.Dns is not null && previous.Dns != current.Dns))
            throw Conflict();
        _ = new LinuxMihomoTunRoutes(address, destination, management, intent.ManagementUserId, intent.RoutingTable);
    }

    private static RuntimePolicyException Conflict() => new("runtime.network_checkpoint_conflict");
}
