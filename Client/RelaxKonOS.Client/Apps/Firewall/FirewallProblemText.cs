using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Privileged;

namespace RelaxKonOS.Client.Apps.Firewall;

/// <summary>
/// Turns the Server's stable firewall problem codes into a sentence that names the cause and the
/// next step. A recognised code must never be shown to the user as a bare token, and an unknown
/// code is preserved verbatim so diagnostics do not lose information.
/// </summary>
public static class FirewallProblemText
{
    /// <summary>Resolves a firewall problem code, falling back to the supplied text when the code is empty.</summary>
    public static string Format(string? problemCode, string fallback)
    {
        if (string.IsNullOrWhiteSpace(problemCode)) return fallback;
        var message = problemCode switch
        {
            "firewall.ufw_not_installed" => LocalizedText.Get("firewall.problem.ufw_not_installed",
                "UFW is not installed on this host. This application does not install or replace the firewall backend."),
            "firewall.unsupported_platform" => LocalizedText.Get("firewall.problem.unsupported_platform",
                "This host platform has no firewall backend this application can manage."),
            "firewall.invalid_rule" => LocalizedText.Get("firewall.problem.invalid_rule",
                "The host rejected this rule. Check the action, direction, protocol, address and port against the host policy."),
            "firewall.invalid_rule_number" => LocalizedText.Get("firewall.problem.invalid_rule_number",
                "That rule number no longer exists on the host. Refresh to read the current rules."),
            "firewall.invalid_default_policy" => LocalizedText.Get("firewall.problem.invalid_default_policy",
                "The host does not accept this default policy for its backend. Refresh to read the current policies."),
            "firewall.operation_failed" => LocalizedText.Get("firewall.problem.operation_failed",
                "The host rejected the change. Refresh to read the current firewall state before trying again."),
            _ => null,
        };
        return message ?? PrivilegedHelperProblemText.FormatOrFallback(problemCode, problemCode);
    }
}
