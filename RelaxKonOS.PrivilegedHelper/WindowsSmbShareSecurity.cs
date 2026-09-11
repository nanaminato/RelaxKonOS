using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

[SupportedOSPlatform("windows")]
internal static class WindowsSmbShareSecurity
{
    public static byte[] CreateDescriptor(IReadOnlyList<SmbSharePermissionRequest> permissions, bool readOnly, bool guest)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var acl = new DiscretionaryAcl(false, false, permissions.Count + 1);
        foreach (var permission in permissions)
        {
            var sid = new SecurityIdentifier(permission.Principal);
            var write = permission.Access == "ReadWrite" && !readOnly;
            acl.AddAccess(AccessControlType.Allow, sid, write ? 0x001F01FF : 0x00120089, InheritanceFlags.None, PropagationFlags.None);
        }
        if (guest)
        {
            // Deny only mutation rights, leaving the read/synchronize rights shared by generic masks intact.
            foreach (var type in new[] { WellKnownSidType.AnonymousSid, WellKnownSidType.BuiltinGuestsSid })
            {
                var sid = new SecurityIdentifier(type, null);
                acl.AddAccess(AccessControlType.Deny, sid, 0x000D0156, InheritanceFlags.None, PropagationFlags.None);
                acl.AddAccess(AccessControlType.Allow, sid, 0x00120089, InheritanceFlags.None, PropagationFlags.None);
            }
        }
        var descriptor = new CommonSecurityDescriptor(false, false, ControlFlags.DiscretionaryAclPresent | ControlFlags.SelfRelative, administrators, administrators, null, acl);
        var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0); return bytes;
    }
}
