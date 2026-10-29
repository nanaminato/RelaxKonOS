using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace RelaxKonOS.PrivilegedHelper;

[SupportedOSPlatform("windows")]
internal static class WindowsUserProcessToken
{
    internal static SafeAccessTokenHandle Create(SafeAccessTokenHandle impersonation, string userSid)
    {
        if (!DuplicateTokenEx(impersonation, 0xF01FF, IntPtr.Zero, 2, 1, out var primary))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try { ConfigureTokenAccess(primary, userSid); return primary; }
        catch { primary.Dispose(); throw; }
    }

    private static void ConfigureTokenAccess(SafeAccessTokenHandle token, string userSid)
    {
        // DuplicateTokenEx is called as SYSTEM. Its default token-object ACL otherwise prevents
        // the resulting user process from querying its own token (for example, whoami fails).
        // Child process/thread objects also need a default ACL for the restricted user's SID.
        var descriptor = new RawSecurityDescriptor($"D:(A;;GA;;;SY)(A;;GA;;;{userSid})");
        var descriptorBytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(descriptorBytes, 0);
        var acl = descriptor.DiscretionaryAcl!;
        var aclBytes = new byte[acl.BinaryLength];
        acl.GetBinaryForm(aclBytes, 0);
        var nativeAcl = Marshal.AllocHGlobal(aclBytes.Length);
        var defaultDacl = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            if (!SetKernelObjectSecurity(token, 4, descriptorBytes))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Marshal.Copy(aclBytes, 0, nativeAcl, aclBytes.Length);
            Marshal.WriteIntPtr(defaultDacl, nativeAcl);
            if (!SetTokenInformation(token, 6, defaultDacl, IntPtr.Size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(defaultDacl); Marshal.FreeHGlobal(nativeAcl); }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint access, IntPtr attributes,
        int impersonationLevel, int tokenType, out SafeAccessTokenHandle primary);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetKernelObjectSecurity(SafeAccessTokenHandle handle, uint securityInformation,
        byte[] securityDescriptor);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        IntPtr information, int informationLength);
}

