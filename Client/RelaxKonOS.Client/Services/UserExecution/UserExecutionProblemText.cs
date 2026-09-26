using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.UserExecution;

namespace RelaxKonOS.Client.Services.UserExecution;

/// <summary>
/// Maps the user-execution boundary onto one localized, actionable message per cause. The Server owns
/// the reason, the client owns the wording: matching the problem type (or the login reason code) keeps
/// the two in step, where appending the Server's English detail to a Chinese status bar does not.
/// </summary>
public static class UserExecutionProblemText
{
    /// <summary>Formats a ProblemDetails type or bare problem code; false when this client has no text
    /// for it and the caller should fall back to the Server detail.</summary>
    public static bool TryFormat(string? problem, out string message)
    {
        if (TryResolveKey(problem, out var key)) { message = LocalizedText.Get(key); return true; }
        message = string.Empty;
        return false;
    }

    /// <summary>
    /// Resolves only the resource key. The mapping is a decision, not a translation, so it is testable
    /// without a running application: tests assert keys, the localization packs supply the text.
    /// </summary>
    public static bool TryResolveKey(string? problem, out string key)
    {
        key = problem switch
        {
            null => string.Empty,
            _ when Matches(problem, UserExecutionProblemTypes.IdentityNotEligible) => IdentityNotEligibleKey,
            _ when Matches(problem, UserExecutionProblemTypes.IdentityNotExecutable) => IdentityNotExecutableKey,
            _ => string.Empty,
        };
        return key.Length > 0;
    }

    /// <summary>
    /// Localized text for a <see cref="ServerExecutionEligibilityDto.Reason"/> reported at login, or
    /// null when the code is unknown to this client version or is not a user-facing refusal.
    /// </summary>
    public static string? ForReason(string? reason)
        => ResolveKeyForReason(reason) is { } key ? LocalizedText.Get(key) : null;

    /// <summary>Key-only form of <see cref="ForReason"/>, for the same reason as above.</summary>
    public static string? ResolveKeyForReason(string? reason) => reason switch
    {
        ServerExecutionEligibilityReasons.ReservedIdentity
            or ServerExecutionEligibilityReasons.SystemAccount
            or ServerExecutionEligibilityReasons.UnverifiedHomeDirectory
            or ServerExecutionEligibilityReasons.WindowsProfileRequired
            => IdentityNotEligibleKey,
        ServerExecutionEligibilityReasons.ServerAccountRequired => IdentityNotExecutableKey,
        _ => null,
    };

    internal const string IdentityNotEligibleKey = "common.problem.identity_not_eligible";
    internal const string IdentityNotExecutableKey = "common.problem.identity_not_executable";

    private static bool Matches(string problem, string suffix)
        => problem.EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(problem, suffix, StringComparison.OrdinalIgnoreCase);
}
