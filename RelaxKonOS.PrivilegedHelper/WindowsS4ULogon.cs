using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Creates a passwordless, local-only MSV1_0 S4U network token. This is available only to the
/// LocalSystem Helper through its trusted LSA connection. The returned token is never cached,
/// serialized, logged, or exposed to the Server.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsS4ULogon
{
    private const uint MsV1_0S4ULogon = 12;
    private const uint CheckLogonHours = 0x2;
    private const int NetworkLogon = 3;
    private const string AuthenticationPackage = "MICROSOFT_AUTHENTICATION_PACKAGE_V1_0";

    public static SafeAccessTokenHandle Logon(string username, string domain)
    {
        using var processName = new AnsiString("RelaxKonOS");
        var status = LsaRegisterLogonProcess(ref processName.Value, out var lsa, out _);
        ThrowIfFailed(status, "register trusted logon process");
        try
        {
            using var packageName = new AnsiString(AuthenticationPackage);
            status = LsaLookupAuthenticationPackage(lsa, ref packageName.Value, out var packageId);
            ThrowIfFailed(status, "resolve MSV1_0 authentication package");

            using var originName = new AnsiString("RelaxKonOS");
            using var authentication = new S4UBuffer(username, domain);
            if (!AllocateLocallyUniqueId(out var sourceId))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not allocate an LSA token source identifier.");
            var source = new TokenSource
            {
                SourceName = [.. "RELAXKON"u8],
                SourceIdentifier = sourceId,
            };

            status = LsaLogonUser(lsa, ref originName.Value, NetworkLogon, packageId,
                authentication.Pointer, authentication.Length, IntPtr.Zero, ref source,
                out var profile, out _, out _, out var token, out _, out var subStatus);
            try
            {
                if (status != 0) ThrowIfFailed(subStatus != 0 ? subStatus : status, "create local S4U token");
                if (token.IsInvalid) throw new InvalidOperationException("LSA returned an invalid S4U token.");
                return token;
            }
            finally
            {
                if (profile != IntPtr.Zero) LsaFreeReturnBuffer(profile);
            }
        }
        finally { LsaDeregisterLogonProcess(lsa); }
    }

    private static void ThrowIfFailed(int status, string operation)
    {
        if (status == 0) return;
        throw new Win32Exception(unchecked((int)LsaNtStatusToWinError(status)),
            $"Could not {operation}.");
    }

    private sealed class AnsiString : IDisposable
    {
        private readonly IntPtr _buffer;
        public LsaString Value;

        public AnsiString(string value)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(value);
            if (bytes.Length > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
            _buffer = Marshal.StringToHGlobalAnsi(value);
            Value = new LsaString { Length = (ushort)bytes.Length, MaximumLength = (ushort)(bytes.Length + 1), Buffer = _buffer };
        }

        public void Dispose() => Marshal.FreeHGlobal(_buffer);
    }

    private sealed class S4UBuffer : IDisposable
    {
        public IntPtr Pointer { get; }
        public uint Length { get; }

        public S4UBuffer(string username, string domain)
        {
            var usernameBytes = System.Text.Encoding.Unicode.GetBytes(username);
            var domainBytes = System.Text.Encoding.Unicode.GetBytes(domain);
            var structureSize = Marshal.SizeOf<Msv1_0S4ULogon>();
            Length = checked((uint)(structureSize + usernameBytes.Length + 2 + domainBytes.Length + 2));
            Pointer = Marshal.AllocHGlobal(checked((int)Length));
            Marshal.Copy(new byte[checked((int)Length)], 0, Pointer, checked((int)Length));
            var usernamePointer = IntPtr.Add(Pointer, structureSize);
            var domainPointer = IntPtr.Add(usernamePointer, usernameBytes.Length + 2);
            Marshal.Copy(usernameBytes, 0, usernamePointer, usernameBytes.Length);
            Marshal.Copy(domainBytes, 0, domainPointer, domainBytes.Length);
            Marshal.StructureToPtr(new Msv1_0S4ULogon
            {
                MessageType = MsV1_0S4ULogon,
                Flags = CheckLogonHours,
                UserPrincipalName = Unicode(usernamePointer, usernameBytes.Length),
                DomainName = Unicode(domainPointer, domainBytes.Length),
            }, Pointer, false);
        }

        private static LsaUnicodeString Unicode(IntPtr pointer, int byteLength)
            => new() { Length = checked((ushort)byteLength), MaximumLength = checked((ushort)(byteLength + 2)), Buffer = pointer };

        public void Dispose()
        {
            if (Pointer == IntPtr.Zero) return;
            Marshal.Copy(new byte[checked((int)Length)], 0, Pointer, checked((int)Length));
            Marshal.FreeHGlobal(Pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Msv1_0S4ULogon
    {
        public uint MessageType, Flags;
        public LsaUnicodeString UserPrincipalName, DomainName;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenSource
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] SourceName;
        public Luid SourceIdentifier;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct QuotaLimits
    {
        public IntPtr PagedPoolLimit, NonPagedPoolLimit, MinimumWorkingSetSize, MaximumWorkingSetSize, PagefileLimit;
        public long TimeLimit;
    }

    [DllImport("secur32.dll")]
    private static extern int LsaRegisterLogonProcess(ref LsaString logonProcessName, out IntPtr lsaHandle,
        out uint securityMode);
    [DllImport("secur32.dll")]
    private static extern int LsaLookupAuthenticationPackage(IntPtr lsaHandle, ref LsaString packageName,
        out uint authenticationPackage);
    [DllImport("secur32.dll")]
    private static extern int LsaLogonUser(IntPtr lsaHandle, ref LsaString originName, int logonType,
        uint authenticationPackage, IntPtr authenticationInformation, uint authenticationInformationLength,
        IntPtr localGroups, ref TokenSource sourceContext, out IntPtr profileBuffer, out uint profileBufferLength,
        out Luid logonId, out SafeAccessTokenHandle token, out QuotaLimits quotas, out int subStatus);
    [DllImport("secur32.dll")]
    private static extern int LsaDeregisterLogonProcess(IntPtr lsaHandle);
    [DllImport("secur32.dll")]
    private static extern int LsaFreeReturnBuffer(IntPtr buffer);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocateLocallyUniqueId(out Luid luid);
    [DllImport("advapi32.dll")]
    private static extern uint LsaNtStatusToWinError(int status);
}
