using System.Security.Claims;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;

public sealed class TestHostFileAuthorizationService(IFileElevationSessionStore? grants = null)
    : IHostFileAuthorizationService
{
    public bool Root { get; set; }
    public bool Administrator { get; set; }

    public bool IsRoot(ClaimsPrincipal principal) => Root;

    public PrivilegedFileAuthorizationSource? Authorize(ClaimsPrincipal principal,
        FileElevationCapability capability, params string[] paths)
    {
        if (Root) return PrivilegedFileAuthorizationSource.HostRoot;
        if (grants?.IsElevated(principal, capability, paths) == true)
            return PrivilegedFileAuthorizationSource.ManualGrant;
        return Administrator ? PrivilegedFileAuthorizationSource.HostAdministrator : null;
    }
}
