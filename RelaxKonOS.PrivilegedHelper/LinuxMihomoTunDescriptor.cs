using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RelaxKonOS.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>A nonpersistent Linux TUN queue. The transaction must persist the recipient and network intent
/// before calling SendTo; descriptor delivery is a single attempt and is not a commit receipt.</summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxMihomoTunDescriptor : IDisposable
{
    private const uint TunSetInterface = 0x400454ca;
    private const ushort ExclusiveTunWithoutPacketInfo = 0x8000 | 0x1000 | 0x0001;
    private const int InterfaceNameSize = 16;
    private const int OpenReadWrite = 2, OpenNonBlocking = 0x800, OpenCloseOnExec = 0x80000;
    private const int OpenNoFollowX64 = 0x20000, OpenNoFollowArm64 = 0x8000;
    private const int SocketLevel = 1, DescriptorRights = 1, NoSignal = 0x4000;
    private readonly SafeFileHandle handle;
    private readonly Guid operationId;
    private int dispatched;

    private LinuxMihomoTunDescriptor(SafeFileHandle handle, Guid operationId, string name, uint interfaceIndex)
    {
        this.handle = handle;
        this.operationId = operationId;
        Name = name;
        InterfaceIndex = interfaceIndex;
    }

    internal string Name { get; }
    internal uint InterfaceIndex { get; }

    internal static LinuxMihomoTunDescriptor Create(Guid operationId, string name)
    {
        if (operationId == Guid.Empty) throw new RuntimePolicyException("runtime.operation_id_invalid");
        var noFollow = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => OpenNoFollowX64,
            Architecture.Arm64 => OpenNoFollowArm64,
            _ => throw new RuntimePolicyException("runtime.network_platform_unsupported")
        };
        if (name.Length is < 1 or >= InterfaceNameSize || !char.IsAsciiLetterOrDigit(name[0])
            || name.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.')))
            throw new RuntimePolicyException("runtime.network_device_invalid");

        var request = new byte[40]; // Linux x64/arm64 ifreq: IFNAMSIZ followed by its native union.
        Encoding.ASCII.GetBytes(name, request);
        BitConverter.TryWriteBytes(request.AsSpan(InterfaceNameSize), ExclusiveTunWithoutPacketInfo);
        var fd = Open("/dev/net/tun", OpenReadWrite | OpenNonBlocking | noFollow | OpenCloseOnExec);
        if (fd < 0) throw new RuntimePolicyException("runtime.network_device_unavailable");
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        try
        {
            if (Ioctl(handle, TunSetInterface, request) != 0)
                throw new RuntimePolicyException("runtime.network_device_unavailable");
            var terminator = Array.IndexOf(request, (byte)0, 0, InterfaceNameSize);
            var index = InterfaceNameToIndex(name);
            if (terminator < 0 || Encoding.ASCII.GetString(request, 0, terminator) != name || index == 0)
                throw new RuntimePolicyException("runtime.network_device_identity_unknown");
            return new(handle, operationId, name, index);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal void SendTo(Socket connection, Guid owner, int expectedProcessId, uint expectedUserId)
    {
        if (owner != operationId) throw new RuntimePolicyException("runtime.recovery_target_conflict");
        var peer = new LinuxPeerAuthorizer([expectedUserId]).Authorize(connection);
        if (!peer.Allowed || expectedProcessId <= 1 || peer.ProcessId != expectedProcessId || peer.UserId != expectedUserId)
            throw new RuntimePolicyException("runtime.network_recipient_mismatch");
        if (Interlocked.Exchange(ref dispatched, 1) != 0)
            throw new RuntimePolicyException("runtime.network_descriptor_already_dispatched");

        var vectorSize = Marshal.SizeOf<IoVector>();
        var controlSize = Marshal.SizeOf<DescriptorControl>();
        const int alignedPayloadSize = 8;
        var buffer = Marshal.AllocHGlobal(alignedPayloadSize + vectorSize + controlSize);
        var reference = false;
        try
        {
            handle.DangerousAddRef(ref reference);
            var vector = buffer + alignedPayloadSize;
            var control = vector + vectorSize;
            Marshal.WriteByte(buffer, (byte)'T');
            Marshal.StructureToPtr(new IoVector(buffer, 1), vector, false);
            Marshal.StructureToPtr(new DescriptorControl(20, SocketLevel, DescriptorRights, handle.DangerousGetHandle().ToInt32()), control, false);
            var message = new MessageHeader { Vector = vector, VectorCount = 1, Control = control, ControlLength = (nuint)controlSize };
            if (SendMessage(connection.SafeHandle, ref message, NoSignal) != 1)
                throw new RuntimePolicyException("runtime.network_descriptor_unconfirmed");
        }
        finally
        {
            if (reference) handle.DangerousRelease();
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct IoVector(nint Data, nuint Length);

    // cmsghdr + one int. cmsg_len excludes trailing alignment; the socket buffer includes it.
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DescriptorControl(nuint Length, int Level, int Type, int Descriptor);

    [StructLayout(LayoutKind.Sequential)]
    private struct MessageHeader
    {
        public nint Address;
        public uint AddressLength;
        public nint Vector;
        public nuint VectorCount;
        public nint Control;
        public nuint ControlLength;
        public int Flags;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(SafeFileHandle descriptor, nuint request, [In, Out] byte[] value);

    [DllImport("libc", EntryPoint = "if_nametoindex", SetLastError = true)]
    private static extern uint InterfaceNameToIndex(string name);

    [DllImport("libc", EntryPoint = "sendmsg", SetLastError = true)]
    private static extern nint SendMessage(SafeSocketHandle socket, ref MessageHeader message, int flags);
}
