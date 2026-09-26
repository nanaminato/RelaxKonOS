using System.Security.Claims;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.HostMode;

namespace RelaxKonOS.Server.Privileged;

/// <summary>Single per-request decision for the source of a privileged file operation.</summary>
public interface IHostFileAuthorizationService
{
    bool IsRoot(ClaimsPrincipal principal);
    PrivilegedFileAuthorizationSource? Authorize(ClaimsPrincipal principal, FileElevationCapability capability,
        params string[] paths);
}

public sealed class HostFileAuthorizationService(IHostAccountPrivilegeService privileges,
    IFileElevationSessionStore grants, IServerModeResolver mode) : IHostFileAuthorizationService
{
    public bool IsRoot(ClaimsPrincipal principal) => privileges.IsRoot(principal);

    public PrivilegedFileAuthorizationSource? Authorize(ClaimsPrincipal principal,
        FileElevationCapability capability, params string[] paths)
    {
        if (paths.Length == 0 || paths.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            return null;
        if (mode.Mode != ServerMode.System) return null;
        var privilege = privileges.Classify(principal);
        if (privilege == HostAccountPrivilege.HostRoot) return PrivilegedFileAuthorizationSource.HostRoot;
        if (privilege == HostAccountPrivilege.HostAdministrator) return PrivilegedFileAuthorizationSource.HostAdministrator;
        return grants.IsElevated(principal, capability, paths)
            ? PrivilegedFileAuthorizationSource.ManualGrant : null;
    }
}
