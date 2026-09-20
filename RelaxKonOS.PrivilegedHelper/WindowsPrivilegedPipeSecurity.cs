using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// The single place that decides which Windows identities may connect to the Helper pipe. Service
/// mode names the installed Server service SID; the developer console host names the configured
/// developer identities. Everything else is refused by the kernel before the protocol runs, which
/// is why a mismatch surfaces as an access denial rather than an authentication failure.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsPrivilegedPipeSecurity
{
    public static PipeSecurity Build(string? serverServiceSid, IEnumerable<string>? developerUserSids)
    {
        var security = new PipeSecurity();
        // The pipe DACL is exactly the list below; inheriting rights from the creating process
        // would silently widen the privileged surface.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        Add(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl);
        Add(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl);
        if (!string.IsNullOrWhiteSpace(serverServiceSid))
            Add(security, new SecurityIdentifier(serverServiceSid), PipeAccessRights.ReadWrite);
        foreach (var sid in developerUserSids ?? [])
            if (!string.IsNullOrWhiteSpace(sid)) Add(security, new SecurityIdentifier(sid), PipeAccessRights.ReadWrite);
        return security;
    }

    private static void Add(PipeSecurity security, SecurityIdentifier identity, PipeAccessRights rights)
    {
        // ReadWrite alone is sufficient for a client to connect and exchange frames; it needs
        // neither Synchronize nor ChangePermissions (verified against a real pipe: ReadWrite
        // connects, a data-only mask such as 0x3 is refused).
        //
        // The DACL is built strongest-first, so an identity that is already authorized never needs
        // a second, weaker rule. Collapsing those keeps the effective list readable: naming the
        // Administrators or the Server account twice must not produce duplicate entries.
        if (security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<PipeAccessRule>()
            .Any(rule => rule.IdentityReference.Equals(identity) && rule.AccessControlType == AccessControlType.Allow)) return;
        security.AddAccessRule(new PipeAccessRule(identity, rights, AccessControlType.Allow));
    }
}
