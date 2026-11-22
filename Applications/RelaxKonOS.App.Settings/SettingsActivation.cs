namespace RelaxKonOS.App.Settings;

internal sealed record SettingsActivation(string Route, string? ApplicationId = null)
{
    private static readonly HashSet<string> Routes = new(StringComparer.OrdinalIgnoreCase)
    {
        "home", "system", "account-security", "personalization", "time-language", "network",
        "apps", "default-apps", "developer", "accessibility", "about", "system/preferences",
        "system/environment", "personalization/colors", "personalization/style",
        "personalization/layout", "personalization/background",
    };

    public static SettingsActivation? Match(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !uri.Scheme.Equals("relaxkonos", StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("settings", StringComparison.OrdinalIgnoreCase))
            return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();

        // An escaped separator cannot change which route was originally addressed.
        if (segments.Any(segment => segment.Contains('/')))
            return null;

        var route = string.Join("/", segments);
        if (Routes.TryGetValue(route, out var canonicalRoute))
            return new(canonicalRoute);

        if (segments is [var parent, var appId, var child]
            && parent.Equals("apps", StringComparison.OrdinalIgnoreCase)
            && child.Equals("permissions", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(appId))
            return new("apps/permissions", appId);

        return null;
    }
}
