using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes;

/// <summary>The host administrator's scope for new managed TUN trials.</summary>
public sealed record LinuxMihomoTunScope(string Device, string Address, string Destination, uint ServerUserId,
    string? DnsRoutingDomain = null)
{
    public const string Path = "/etc/relaxkonos/mihomo-tun-network.json";
    private const string Unavailable = "runtime.network_scope_unavailable";
    private const int MaximumBytes = 4096;
    private const int PathOnly = 0x200000, NonBlocking = 0x800, CloseOnExec = 0x80000;
    private const int Directory = 0x4000, RegularFile = 0x8000;

    [SupportedOSPlatform("linux")]
    public static LinuxMihomoTunScope Read()
    {
        try
        {
            using var root = Open(-100, "/", PathOnly, Directory);
            using var etc = Open(Descriptor(root), "etc", PathOnly, Directory);
            using var configuration = Open(Descriptor(etc), "relaxkonos", PathOnly, Directory);
            using var handle = Open(Descriptor(configuration), "mihomo-tun-network.json", NonBlocking, RegularFile);
            using var stream = new FileStream(handle, FileAccess.Read);
            var size = stream.Length;
            if (size is <= 0 or > MaximumBytes) throw new RuntimePolicyException(Unavailable);
            var bytes = new byte[(int)size];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new RuntimePolicyException(Unavailable);
            var scope = RuntimeJson.Read<LinuxMihomoTunScope>(bytes);
            if (!MihomoRuntimeMaterial.ValidTunScope(scope.Device, scope.Address, scope.Destination, scope.DnsRoutingDomain)
                || scope.ServerUserId == 0)
                throw new RuntimePolicyException(Unavailable);
            return scope;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or RuntimePolicyException { ProblemCode: "runtime.metadata_invalid" })
        {
            throw new RuntimePolicyException(Unavailable);
        }
    }

    public bool Matches(MihomoTunNetworkIntent intent) =>
        Device == intent.Device && Address == intent.Address && Destination == intent.Destination
        && ServerUserId == intent.ManagementUserId && DnsRoutingDomain == intent.DnsRoutingDomain;

    private static SafeFileHandle Open(int parent, string name, int access, int type)
    {
        var noFollow = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 1 << 17,
            Architecture.Arm64 => 1 << 15,
            _ => throw new RuntimePolicyException(Unavailable)
        };
        var descriptor = openat(parent, name, access | noFollow | CloseOnExec);
        if (descriptor < 0) throw new RuntimePolicyException(Unavailable);
        var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        const uint fields = 0xb; // STATX_TYPE | STATX_MODE | STATX_UID
        if (statx(descriptor, string.Empty, 0x1000, fields, out var stat) == 0
            && (stat.Mask & fields) == fields && stat.Uid == 0 && (stat.Mode & 0xf012) == type)
            return handle;
        handle.Dispose();
        throw new RuntimePolicyException(Unavailable);
    }

    private static int Descriptor(SafeFileHandle handle) => handle.DangerousGetHandle().ToInt32();

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct FileStat
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
    }

    [DllImport("libc")]
    private static extern int openat(int parent, string path, int flags);

    [DllImport("libc")]
    private static extern int statx(int descriptor, string path, int flags, uint mask, out FileStat stat);
}
