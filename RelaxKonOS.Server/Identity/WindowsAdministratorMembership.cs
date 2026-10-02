using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace RelaxKonOS.Server.Identity;

/// <summary>
/// Tests whether a Windows account is an Administrator even when UAC supplied its filtered,
/// non-elevated token. The limited-token state is inspected only; no elevation prompt or
/// privileged work is performed here.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsAdministratorMembership
{
    private const int TokenElevationType = 18;
    private const int TokenElevationTypeLimited = 3;

    /// <summary>Resolves current account groups by canonical SID, including indirect/domain
    /// membership. No password, process token, or impersonation is created by the application.</summary>
    public static bool IsAccountAdministrator(string sidValue)
    {
        var sid = new SecurityIdentifier(sidValue);
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        IntPtr manager = IntPtr.Zero, context = IntPtr.Zero, groups = IntPtr.Zero;
        try
        {
            // AUTHZ_RM_FLAG_NO_AUDIT: this is a membership query; operation auditing is separate.
            if (!AuthzInitializeResourceManager(1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, null, out manager)
                || !AuthzInitializeContextFromSid(0, bytes, manager, IntPtr.Zero, default, IntPtr.Zero, out context))
                throw new InvalidOperationException("Host administrator policy is unavailable.");
            AuthzGetInformationFromContext(context, 2, 0, out var size, IntPtr.Zero);
            if (size < IntPtr.Size || size > 16 * 1024 * 1024)
                throw new InvalidOperationException("Host administrator policy is unavailable.");
            groups = Marshal.AllocHGlobal(checked((int)size));
            if (!AuthzGetInformationFromContext(context, 2, size, out _, groups))
                throw new InvalidOperationException("Host administrator policy is unavailable.");
            var count = Marshal.ReadInt32(groups);
            var stride = Marshal.SizeOf<SidAndAttributes>();
            // TOKEN_GROUPS aligns its first SID_AND_ATTRIBUTES to pointer alignment.
            if (count < 0 || (long)IntPtr.Size + (long)count * stride > size)
                throw new InvalidOperationException("Invalid host administrator policy result.");
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            for (var index = 0; index < count; index++)
            {
                var entry = Marshal.PtrToStructure<SidAndAttributes>(IntPtr.Add(groups, IntPtr.Size + index * stride));
                // UAC can mark Administrators as deny-only; membership still means the account
                // can authorize a fixed Helper operation, never that its ordinary token is elevated.
                if (entry.Sid != IntPtr.Zero && new SecurityIdentifier(entry.Sid).Equals(administrators)) return true;
            }
            return false;
        }
        finally
        {
            if (groups != IntPtr.Zero) Marshal.FreeHGlobal(groups);
            if (context != IntPtr.Zero) AuthzFreeContext(context);
            if (manager != IntPtr.Zero) AuthzFreeResourceManager(manager);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [DllImport("authz.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AuthzInitializeResourceManager(uint flags, IntPtr accessCheck, IntPtr computeGroups,
        IntPtr freeGroups, string? name, out IntPtr manager);
    [DllImport("authz.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AuthzInitializeContextFromSid(uint flags, byte[] sid, IntPtr manager, IntPtr expiration,
        Luid identifier, IntPtr dynamicGroups, out IntPtr context);
    [DllImport("authz.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AuthzGetInformationFromContext(IntPtr context, int informationClass, uint size,
        out uint required, IntPtr buffer);
    [DllImport("authz.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AuthzFreeContext(IntPtr context);
    [DllImport("authz.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AuthzFreeResourceManager(IntPtr manager);

    public static bool IsAdministratorOrCanElevate(WindowsIdentity identity)
    {
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return true;
        var token = identity.AccessToken;
        if (token.IsInvalid || !GetTokenInformation(token.DangerousGetHandle(), TokenElevationType,
                out var elevationType, sizeof(int), out _))
            return false;

        // A limited token has a linked, elevated administrator token. This is the normal
        // token presented by an administrator account after UAC filtering.
        return elevationType == TokenElevationTypeLimited;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        out int tokenInformation, int tokenInformationLength, out int returnLength);
}
