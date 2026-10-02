using RelaxKonOS.Protocol.ProcessGuardian;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.ProcessGuardian;

/// <summary>
/// Applies the deliberately small RunAs rule at the HTTP boundary.  It never retains an
/// administrator password and never sends it to the separately-running Guardian Agent.
/// </summary>
public interface IRunAsAuthorizationService
{
    RunAsAuthorizationResult Authorize(string requester, string? requestedRunAs, RunAsAdministratorApproval? approval);
}

public sealed record RunAsAuthorizationResult(bool Success, string ProblemCode, string? RunAs = null, string? StableIdentity = null);

public sealed class RunAsAuthorizationService(IIdentityProvider identities, IServerModeResolver mode, IHostAdministratorAuthenticator administrators) : IRunAsAuthorizationService
{
    public RunAsAuthorizationResult Authorize(string requester, string? requestedRunAs, RunAsAdministratorApproval? approval)
    {
        var target = string.IsNullOrWhiteSpace(requestedRunAs) ? requester.Trim() : requestedRunAs.Trim();
        if (string.IsNullOrWhiteSpace(requester) || string.IsNullOrWhiteSpace(target) || target.IndexOf('\0') >= 0)
            return new RunAsAuthorizationResult(false, "guardian.run_as_invalid_account");

        // A User Mode Agent can only ever execute as its owning Unix account. Do this at the
        // Server boundary as well as in the Agent so a manually forged IPC request has no path
        // to runuser/sudo or an administrator-password fallback.
        if (mode.Mode == RelaxKonOS.Protocol.Common.ServerMode.User && !SameAccount(requester, target))
            return new RunAsAuthorizationResult(false, "guardian.cross_user_unavailable");

        try
        {
            // This resolves Linux users through NSS and validates malformed Windows identities.
            // The original normalized spelling remains the launch identity passed to the Agent.
            var identity = identities.GetUserInfo(target);
            if (string.IsNullOrWhiteSpace(identity.Uid))
                return new RunAsAuthorizationResult(false, "guardian.run_as_invalid_account");

            // Persist the provider's canonical launch name and immutable account identity. The
            // Agent resolves this pair again immediately before every launch, so a later NSS/SAM
            // name reassignment cannot make an existing workload run as a different account.
            target = identity.Username;
            var stableIdentity = identity.Uid;

            // Every cross-account launch requires a fresh administrator confirmation. This
            // deliberately includes a logged-in root/Administrator switching to another user.
            if (SameAccount(requester, target))
                return new RunAsAuthorizationResult(true, string.Empty, target, stableIdentity);

            if (approval is null || string.IsNullOrWhiteSpace(approval.Username) || string.IsNullOrEmpty(approval.Password))
                return new RunAsAuthorizationResult(false, "guardian.run_as_admin_authentication_required");

            // Deliberately collapse bad passwords, missing accounts, and non-administrators to one
            // result, so this endpoint cannot be used to enumerate administrator accounts.
            var verified = administrators.Authenticate(requester, approval.Username, approval.Password);
            if (!verified.Succeeded)
                return new RunAsAuthorizationResult(false, "guardian.run_as_admin_authentication_failed");

            return new RunAsAuthorizationResult(true, string.Empty, target, stableIdentity);
        }
        catch (ArgumentException) { return new RunAsAuthorizationResult(false, "guardian.run_as_invalid_account"); }
        catch (KeyNotFoundException) { return new RunAsAuthorizationResult(false, "guardian.run_as_invalid_account"); }
        catch (InvalidOperationException) { return new RunAsAuthorizationResult(false, "guardian.run_as_invalid_account"); }
    }

    private static bool SameAccount(string left, string right) => OperatingSystem.IsWindows()
        ? string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase)
        : string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);

}
