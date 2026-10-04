using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Server.Identity;

/// <summary>Account scope after loopback Negotiate; System Mode additionally requires a Windows administrator.</summary>
internal static class WindowsOwnerBootstrapPolicy
{
    public static bool AllowsBootstrap(string? callerSid, string? serverSid, ServerMode mode, bool administrator)
        => (mode == ServerMode.User || administrator) && AllowsAccount(callerSid, serverSid, mode);
    public static bool AllowsAccount(string? callerSid, string? serverSid, ServerMode mode)
    {
        if (string.IsNullOrWhiteSpace(callerSid) || string.IsNullOrWhiteSpace(serverSid) || IsServiceIdentity(callerSid))
            return false;
        return string.Equals(callerSid, serverSid, StringComparison.OrdinalIgnoreCase) ||
            mode == ServerMode.System && IsServiceIdentity(serverSid);
    }

    private static bool IsServiceIdentity(string sid) => sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20";
}
