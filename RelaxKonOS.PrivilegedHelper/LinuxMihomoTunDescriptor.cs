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
    private const uint TunGetInterface = 0x800454d2;
    private const ushort ExclusiveTunWithoutPacketInfo = 0x8000 | 0x1000 | 0x0001;
    private const int InterfaceNameSize = 16;
    private const int OpenReadWrite = 2, OpenNonBlocking = 0x800, OpenCloseOnExec = 0x80000;
    private const int OpenNoFollowX64 = 0x20000, OpenNoFollowArm64 = 0x8000;
    private const int SocketLevel = 1, DescriptorRights = 1, NoSignal = 0x4000;
    private const int ControlTruncated = 0x8, PayloadTruncated = 0x20, ReceiveCloseOnExec = 0x40000000;
    private const int OperationIdLength = 16, PayloadSize = 1 + OperationIdLength, AlignedPayloadSize = 24;
    private const int SingleDescriptorLength = 20;
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
    internal Guid Owner => operationId;

    internal static bool ValidName(string name) => name.Length is >= 1 and < InterfaceNameSize
        && char.IsAsciiLetterOrDigit(name[0])
        && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');

    internal static LinuxMihomoTunDescriptor Create(Guid operationId, string name)
    {
        if (operationId == Guid.Empty) throw new RuntimePolicyException("runtime.operation_id_invalid");
        var noFollow = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => OpenNoFollowX64,
            Architecture.Arm64 => OpenNoFollowArm64,
            _ => throw new RuntimePolicyException("runtime.network_platform_unsupported")
        };
        if (!ValidName(name))
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
        RequirePeer(connection, expectedProcessId, expectedUserId, "runtime.network_recipient_mismatch");
        if (Interlocked.Exchange(ref dispatched, 1) != 0)
            throw new RuntimePolicyException("runtime.network_descriptor_already_dispatched");

        var buffer = AllocateMessage(out var message);
        var reference = false;
        try
        {
            handle.DangerousAddRef(ref reference);
            Marshal.WriteByte(buffer, (byte)'T');
            Marshal.Copy(operationId.ToByteArray(), 0, buffer + 1, OperationIdLength);
            Marshal.StructureToPtr(new DescriptorControl(SingleDescriptorLength, SocketLevel, DescriptorRights,
                handle.DangerousGetHandle().ToInt32()), message.Control, false);
            Marshal.WriteInt32(message.Control, SingleDescriptorLength, 0);
            if (SendMessage(connection.SafeHandle, ref message, NoSignal) != PayloadSize)
                throw new RuntimePolicyException("runtime.network_descriptor_unconfirmed");
        }
        finally
        {
            if (reference) handle.DangerousRelease();
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static SafeFileHandle ReceiveFrom(Socket connection, Guid owner, int expectedBrokerProcessId, uint expectedBrokerUserId,
        string expectedName, uint expectedIndex)
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new RuntimePolicyException("runtime.network_platform_unsupported");
        if (owner == Guid.Empty) throw new RuntimePolicyException("runtime.operation_id_invalid");
        RequirePeer(connection, expectedBrokerProcessId, expectedBrokerUserId, "runtime.network_broker_mismatch");
        if (!connection.Poll(TimeSpan.FromSeconds(8), SelectMode.SelectRead))
            throw new RuntimePolicyException("runtime.network_descriptor_unconfirmed");

        var buffer = AllocateMessage(out var message);
        SafeFileHandle? descriptor = null;
        try
        {
            var bytes = ReceiveMessage(connection.SafeHandle, ref message, ReceiveCloseOnExec);
            if (bytes < 0 || message.ControlLength < SingleDescriptorLength)
                throw new RuntimePolicyException("runtime.network_descriptor_unconfirmed");
            var control = Marshal.PtrToStructure<DescriptorControl>(message.Control);
            if (control.Level != SocketLevel || control.Type != DescriptorRights)
                throw new RuntimePolicyException("runtime.network_descriptor_invalid");
            descriptor = new SafeFileHandle(control.Descriptor, ownsHandle: true);
            // The aligned buffer can hold a second int. Close it even when rejecting a malformed handoff.
            if (control.Length >= 24 && message.ControlLength >= 24)
                using (new SafeFileHandle(Marshal.ReadInt32(message.Control, SingleDescriptorLength), ownsHandle: true)) { }
            if (bytes != PayloadSize || Marshal.ReadByte(buffer) != 'T' || control.Length != SingleDescriptorLength
                || (message.Flags & (ControlTruncated | PayloadTruncated)) != 0)
                throw new RuntimePolicyException("runtime.network_descriptor_invalid");
            var identity = new byte[OperationIdLength];
            Marshal.Copy(buffer + 1, identity, 0, identity.Length);
            if (new Guid(identity) != owner) throw new RuntimePolicyException("runtime.recovery_target_conflict");

            var request = new byte[40];
            if (Ioctl(descriptor, TunGetInterface, request) != 0)
                throw new RuntimePolicyException("runtime.network_device_identity_unknown");
            var terminator = Array.IndexOf(request, (byte)0, 0, InterfaceNameSize);
            const ushort deviceFlags = 0x0001 | 0x0002 | 0x0800 | 0x1000;
            if (terminator < 0 || Encoding.ASCII.GetString(request, 0, terminator) != expectedName
                || (BitConverter.ToUInt16(request, InterfaceNameSize) & deviceFlags) != 0x1001
                || expectedIndex == 0 || InterfaceNameToIndex(expectedName) != expectedIndex)
                throw new RuntimePolicyException("runtime.network_device_identity_unknown");

            var result = descriptor;
            descriptor = null;
            return result;
        }
        finally
        {
            descriptor?.Dispose();
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void RequirePeer(Socket connection, int expectedProcessId, uint expectedUserId, string problem)
    {
        var peer = new LinuxPeerAuthorizer([expectedUserId]).Authorize(connection);
        if (!peer.Allowed || expectedProcessId <= 1 || peer.ProcessId != expectedProcessId || peer.UserId != expectedUserId)
            throw new RuntimePolicyException(problem);
    }

    private static nint AllocateMessage(out MessageHeader message)
    {
        var vectorSize = Marshal.SizeOf<IoVector>();
        var controlSize = Marshal.SizeOf<DescriptorControl>();
        var buffer = Marshal.AllocHGlobal(AlignedPayloadSize + vectorSize + controlSize);
        var vector = buffer + AlignedPayloadSize;
        Marshal.StructureToPtr(new IoVector(buffer, PayloadSize), vector, false);
        message = new() { Vector = vector, VectorCount = 1, Control = vector + vectorSize, ControlLength = (nuint)controlSize };
        return buffer;
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

    [DllImport("libc", EntryPoint = "recvmsg", SetLastError = true)]
    private static extern nint ReceiveMessage(SafeSocketHandle socket, ref MessageHeader message, int flags);
}
