using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Databases;

internal static class DatabaseInstallationText
{
    public static InstallationTaskText Create(ISystemLanguage language)
    {
        string Text(string key) => DatabaseLocalization.Resolve(language, key, key);
        string Format(string? code) => code switch
        {
            null or "" => Text("installation.problem.unknown"),
            "elevation-password-required" => Text("settings.host_time.password_required"),
            "elevation-password-invalid" => Text("settings.host_time.password_invalid"),
            "elevation-account-not-administrator" => Text("settings.host_time.administrator_required"),
            _ => DatabaseLocalization.Resolve(language, code,
                DatabaseLocalization.Resolve(language, "installation.problem." + code.Replace("installation.", "", StringComparison.Ordinal),
                    Text("installation.problem.unknown")))
        };
        return new(Text, Format, _ => false,
            error => error is InstallationAuthorizationException authorization ? authorization.ProblemCode : null);
    }
}
