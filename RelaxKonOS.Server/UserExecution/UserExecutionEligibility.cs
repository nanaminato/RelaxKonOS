using System.Runtime.InteropServices;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Identity;

namespace RelaxKonOS.Server.UserExecution;

/// <summary>
/// Why an identity may not be executed as. One reason maps to one stable code and one actionable
/// message, and every rule lives here so that signing in and executing can never disagree: the login
/// response tells the user what the first folder open would otherwise tell them, far too late.
/// </summary>
public enum UserExecutionIneligibleReason
{
    None,
    /// <summary>Root or another reserved OS identity (Linux uid 0, nobody 65534).</summary>
    ReservedIdentity,
    /// <summary>A system account below the UID floor this Server requires (System Mode, Linux uid &lt; 1000).</summary>
    SystemAccount,
    /// <summary>No absolute home directory, so the account owns no profile to execute inside.</summary>
    UnverifiedHomeDirectory,
    /// <summary>User Mode runs only as the Server's own account, and this is a different one.</summary>
    ServerAccountRequired,
    /// <summary>Not a local Windows account with a verified profile (System Mode requires one).</summary>
    WindowsProfileRequired,
    /// <summary>An identity space this Server cannot execute in at all.</summary>
    UnsupportedPlatform,
}

/// <summary>Result of the eligibility rule. <see cref="ReasonCode"/> is wire data for clients;
/// the message stays on the Server because client text lives in the localization packs.</summary>
public sealed record UserExecutionEligibility(UserExecutionIneligibleReason Reason)
{
    public static readonly UserExecutionEligibility Eligible = new(UserExecutionIneligibleReason.None);

    public bool Available => Reason == UserExecutionIneligibleReason.None;

    /// <summary>Stable kebab-case code for the login response, or null when the identity is eligible.</summary>
    public string? ReasonCode => Reason switch
    {
        UserExecutionIneligibleReason.None => null,
        UserExecutionIneligibleReason.ReservedIdentity => ServerExecutionEligibilityReasons.ReservedIdentity,
        UserExecutionIneligibleReason.SystemAccount => ServerExecutionEligibilityReasons.SystemAccount,
        UserExecutionIneligibleReason.UnverifiedHomeDirectory => ServerExecutionEligibilityReasons.UnverifiedHomeDirectory,
        UserExecutionIneligibleReason.ServerAccountRequired => ServerExecutionEligibilityReasons.ServerAccountRequired,
        UserExecutionIneligibleReason.WindowsProfileRequired => ServerExecutionEligibilityReasons.WindowsProfileRequired,
        _ => ServerExecutionEligibilityReasons.UnsupportedPlatform,
    };

    /// <summary>
    /// The refusal text for the API detail. It names the reason and the way out, because a client can
    /// only add a prefix to it, never repair it.
    /// </summary>
    public string Describe(PlatformUserInfo identity) => Reason switch
    {
        UserExecutionIneligibleReason.ReservedIdentity =>
            $"The host account '{identity.Username}' (uid {identity.Uid}) is a reserved OS identity, so this Server cannot "
            + "execute ordinary file, terminal or Git operations as it. Sign in with a regular host account whose uid is "
            + "1000 or higher; administrator elevation stays available for protected operations.",
        UserExecutionIneligibleReason.SystemAccount =>
            $"The host account '{identity.Username}' has uid {identity.Uid}, below the 1000 this Server requires for "
            + "ordinary operations. Sign in with a regular host account.",
        UserExecutionIneligibleReason.UnverifiedHomeDirectory =>
            $"The host account '{identity.Username}' has no absolute home directory, so this Server has no profile to "
            + "execute ordinary operations in. Ask an administrator to repair that account.",
        UserExecutionIneligibleReason.ServerAccountRequired =>
            $"This Server runs in User Mode and executes ordinary operations only as its own OS account "
            + $"'{Environment.UserName}'. Sign in with that account.",
        UserExecutionIneligibleReason.WindowsProfileRequired =>
            "Ordinary operations require a local Windows account with a verified user profile; domain accounts and "
            + "accounts without a profile are not eligible. Sign in with a local account.",
        _ => "The OS identity is not supported for user execution.",
    };
}

/// <summary>
/// The single eligibility rule. It answers for one identity on one Server mode, never for the host:
/// the same account is eligible on a System Mode Server and refused on a User Mode one.
/// </summary>
public static class UserExecutionEligibilityRules
{
    public static UserExecutionEligibility Evaluate(PlatformUserInfo identity, ServerMode mode)
    {
        if (identity.Platform == HostPlatformKind.Linux)
        {
            if (!uint.TryParse(identity.Uid, out var uid) || uid is 0 or 65534)
                return new UserExecutionEligibility(UserExecutionIneligibleReason.ReservedIdentity);
            if (mode == ServerMode.System && !UserExecutionProtocol.IsEligibleLinuxUserId(uid))
                return new UserExecutionEligibility(UserExecutionIneligibleReason.SystemAccount);
            if (!UserExecutionProtocol.IsEligibleHomeDirectory(HostPlatformKind.Linux, identity.HomeDirectory))
                return new UserExecutionEligibility(UserExecutionIneligibleReason.UnverifiedHomeDirectory);
            if (mode == ServerMode.User && !IsServerEffectiveUnixUser(uid))
                return new UserExecutionEligibility(UserExecutionIneligibleReason.ServerAccountRequired);
            return UserExecutionEligibility.Eligible;
        }

        if (identity.Platform == HostPlatformKind.Windows)
        {
            var account = identity.Username.Split('\\', 2);
            if (mode != ServerMode.System || account.Length != 2
                || !account[0].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(identity.Uid) || !identity.Uid.StartsWith("S-1-5-", StringComparison.Ordinal)
                || !UserExecutionProtocol.IsEligibleHomeDirectory(HostPlatformKind.Windows, identity.HomeDirectory))
                return new UserExecutionEligibility(UserExecutionIneligibleReason.WindowsProfileRequired);
            return UserExecutionEligibility.Eligible;
        }

        return new UserExecutionEligibility(UserExecutionIneligibleReason.UnsupportedPlatform);
    }

    /// <summary>
    /// The Server's effective Unix user exists only on Linux. On any other host a Linux identity can
    /// never be it, so User Mode refuses it instead of probing libc on a platform that has none.
    /// </summary>
    private static bool IsServerEffectiveUnixUser(uint uid) => OperatingSystem.IsLinux() && uid == geteuid();

    [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint geteuid();
}
