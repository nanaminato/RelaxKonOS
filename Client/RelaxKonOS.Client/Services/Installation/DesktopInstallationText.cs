using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Privileged;

namespace RelaxKonOS.Client.Services.Installation;

public static class DesktopInstallationText
{
    public static InstallationTaskText Instance { get; } = new(LocalizedText.Get, FormatProblemCode,
        code => PrivilegedHelperProblemText.TryFormat(code, out _),
        error => error is RelaxKonOSAuthException auth ? auth.Type.Split('/').Last() : null);

    /// <summary>
    /// Installation operations retain their domain problem code in the durable task record.
    /// Resolve it through that domain's resources instead of exposing a transport-facing code
    /// in the shared installation progress panel.
    /// </summary>
    public static string FormatProblemCode(string? problemCode)
    {
        if (PrivilegedHelperProblemText.TryFormat(problemCode, out var helperMessage)) return helperMessage;
        if (string.IsNullOrWhiteSpace(problemCode)) return LocalizedText.Get("installation.problem.unknown");
        // A component change is refused or fails with a component lifecycle code, and that code is the
        // host's actual answer (package unresolved, candidate changed, install failed, version
        // mismatch). The shared banner has no resource domain for it, so resolve it through the
        // component center's own table instead of falling back to "installation failed".
        if (ComponentProblemText.IsComponentCode(problemCode)) return LocalizedText.Get(ComponentProblemText.Key(problemCode));

        if (problemCode == "elevation-password-required")
            return LocalizedText.Get("settings.host_time.password_required");
        if (problemCode == "elevation-password-invalid")
            return LocalizedText.Get("settings.host_time.password_invalid");
        if (problemCode == "elevation-account-not-administrator")
            return LocalizedText.Get("settings.host_time.administrator_required");
        if (problemCode.StartsWith("elevation-", StringComparison.Ordinal) || problemCode.StartsWith("host-administrator-authentication-", StringComparison.Ordinal))
            return LocalizedText.Get("settings.host_time.password_check_failed");

        const string listenPortPrefix = "webserver.listen_port_unavailable.";
        if (problemCode.StartsWith(listenPortPrefix, StringComparison.Ordinal)
            && ushort.TryParse(problemCode.AsSpan(listenPortPrefix.Length), out var port) && port != 0)
            return LocalizedText.Format("webservers.problem.listen_port_unavailable", port);

        var candidates = new List<string>();
        if (problemCode.StartsWith("installation.", StringComparison.Ordinal))
            candidates.Add("installation.problem." + problemCode["installation.".Length..]);
        else
        {
            var separator = problemCode.IndexOf('.');
            if (separator > 0 && separator < problemCode.Length - 1)
            {
                var domain = problemCode[..separator] switch
                {
                    "tunnel" => "tunnels",
                    "nginx" => "webservers",
                    "smb" => "file_services",
                    _ => problemCode[..separator],
                };
                candidates.Add(domain + ".problem." + problemCode[(separator + 1)..].Replace('.', '_'));
            }
        }
        candidates.Add("installation.problem." + problemCode);
        foreach (var key in candidates)
        {
            var localized = LocalizedText.Get(key);
            if (!string.Equals(localized, key, StringComparison.Ordinal)) return localized;
        }
        return LocalizedText.Get("installation.problem.unknown");
    }
}
