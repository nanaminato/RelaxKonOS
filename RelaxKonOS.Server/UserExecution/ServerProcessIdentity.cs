using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.UserExecution;

namespace RelaxKonOS.Server.UserExecution;

/// <summary>
/// The Server process's own OS identity, used as the load-bearing guard of the
/// <see cref="UserExecutionBackend.LocalIdentity"/> backend.
/// </summary>
/// <remarks>
/// Executing in-process is only equivalent to executing as the effective user while the two are the
/// same account: then access control is still decided by the target account, because the process
/// <em>is</em> that account. The moment they differ, in-process execution would silently run as the
/// Server account — precisely the fallback the effective-OS-user boundary forbids — so the guard
/// must refuse rather than degrade. Everything else (environment name, non-root) is defence in
/// depth on top of this comparison.
/// </remarks>
public static class ServerProcessIdentity
{
    /// <summary>
    /// True when <paramref name="identity"/> is the account this process is already running as.
    /// A Windows identity is compared by canonical SID (case-insensitive) and a Linux identity by
    /// the effective UID; a foreign platform never matches, because a Server cannot be running as an
    /// account of another platform's identity space.
    /// </summary>
    public static bool Matches(UserExecutionIdentity identity) => identity.Platform switch
    {
        HostPlatformKind.Windows => OperatingSystem.IsWindows() && MatchesWindowsSid(identity.StableIdentity),
        HostPlatformKind.Linux => OperatingSystem.IsLinux() && MatchesEffectiveUnixUser(identity.StableIdentity),
        _ => false,
    };

    /// <summary>Describes the process identity for startup diagnostics and refusal messages.</summary>
    public static string Describe()
    {
        if (OperatingSystem.IsWindows()) return WindowsSid() ?? "unknown Windows account";
        if (OperatingSystem.IsLinux()) return $"uid {geteuid()}";
        return "unsupported host platform";
    }

    /// <summary>
    /// The process identity in the same form a <see cref="UserExecutionIdentity.StableIdentity"/>
    /// uses (canonical SID or UID), or null when the host platform is unsupported or its token
    /// cannot be read. Callers must treat null as "no identity", never as a match.
    /// </summary>
    public static string? CurrentStableIdentity()
    {
        if (OperatingSystem.IsWindows()) return WindowsSid();
        if (OperatingSystem.IsLinux()) return geteuid().ToString();
        return null;
    }

    /// <summary>
    /// A Linux host rejects <c>root</c> outright: the debugging backend exists so that a developer
    /// can work as themselves, never so that the whole Server runs privileged.
    /// </summary>
    public static bool IsPrivileged() => OperatingSystem.IsLinux() && geteuid() == 0;

    private static bool MatchesWindowsSid(string stableIdentity) =>
        WindowsSid() is { } sid && string.Equals(sid, stableIdentity, StringComparison.OrdinalIgnoreCase);

    [SupportedOSPlatform("windows")]
    private static string? WindowsSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch (Exception exception) when (exception is SystemException or InvalidOperationException)
        {
            // An unreadable token must refuse, not pass: the guard may never fail open.
            return null;
        }
    }

    private static bool MatchesEffectiveUnixUser(string stableIdentity) =>
        uint.TryParse(stableIdentity, out var uid) && uid == geteuid() && uid != 0;

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();
}
