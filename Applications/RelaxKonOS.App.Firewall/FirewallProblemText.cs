namespace RelaxKonOS.App.Firewall;

/// <summary>
/// Turns the Server's stable firewall problem codes into a sentence that names the cause and the
/// next step. A recognised code must never be shown to the user as a bare token, and an unknown
/// code is preserved verbatim so diagnostics do not lose information.
/// </summary>
public sealed class FirewallProblemText(Func<string, string, string> localize, Func<string, string, string> formatHostProblem)
{
    /// <summary>Resolves a firewall problem code, falling back to the supplied text when the code is empty.</summary>
    public string Format(string? problemCode, string fallback)
    {
        if (string.IsNullOrWhiteSpace(problemCode)) return fallback;
        var message = problemCode switch
        {
            "firewall.ufw_not_installed" => localize("firewall.problem.ufw_not_installed",
                "UFW is not installed on this host. This application does not install or replace the firewall backend."),
            "firewall.unsupported_platform" => localize("firewall.problem.unsupported_platform",
                "This host platform has no firewall backend this application can manage."),
            "firewall.invalid_rule" => localize("firewall.problem.invalid_rule",
                "The host rejected this rule. Check the action, direction, protocol, address and port against the host policy."),
            "firewall.invalid_rule_number" => localize("firewall.problem.invalid_rule_number",
                "That rule number no longer exists on the host. Refresh to read the current rules."),
            "firewall.invalid_default_policy" => localize("firewall.problem.invalid_default_policy",
                "The host does not accept this default policy for its backend. Refresh to read the current policies."),
            "firewall.operation_failed" => localize("firewall.problem.operation_failed",
                "The host rejected the change. Refresh to read the current firewall state before trying again."),
            _ => null,
        };
        return message ?? formatHostProblem(problemCode, problemCode);
    }
}
