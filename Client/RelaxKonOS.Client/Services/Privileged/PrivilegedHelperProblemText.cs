using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Services.Privileged;

/// <summary>Maps every public representation of an unavailable local privilege boundary to one actionable message.</summary>
public static class PrivilegedHelperProblemText
{
    public static bool TryFormat(string? problem, out string message)
    {
        if (IsUnavailable(problem))
        {
            message = LocalizedText.Get("common.problem.privileged_helper_unavailable");
            return true;
        }

        message = string.Empty;
        return false;
    }

    public static string FormatOrFallback(string? problem, string fallback)
        => TryFormat(problem, out var message) ? message : string.IsNullOrWhiteSpace(problem) ? fallback : problem;

    private static bool IsUnavailable(string? problem)
    {
        if (string.IsNullOrWhiteSpace(problem)) return false;
        return problem.EndsWith("/privileged-helper-unavailable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(problem, "privileged-helper-unavailable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(problem, "firewall.privileged_proxy_required", StringComparison.OrdinalIgnoreCase)
            || string.Equals(problem, "proxy.privileged_operation_unavailable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(problem, "git.privileged_helper_unavailable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(problem, "guardian.privileged_helper_unavailable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(problem, "webserver.privileged_helper_unavailable", StringComparison.OrdinalIgnoreCase);
    }
}
