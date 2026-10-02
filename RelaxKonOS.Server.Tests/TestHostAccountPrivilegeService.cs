using System.Security.Claims;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Privileged;

public sealed class TestHostAccountPrivilegeService : IHostAccountPrivilegeService
{
    public HostAccountPrivilege Level { get; set; }
    public bool Unavailable { get; set; }
    public bool IsRoot(ClaimsPrincipal principal) => Classify(principal) == HostAccountPrivilege.HostRoot;
    public HostAccountPrivilege Classify(PlatformUserInfo identity) => Level;
    public HostAccountPrivilege Classify(ClaimsPrincipal principal)
    {
        if (Unavailable) throw new InvalidOperationException("Host policy unavailable");
        return principal.FindFirst("amr")?.Value == "system" ? Level : HostAccountPrivilege.StandardUser;
    }
}
