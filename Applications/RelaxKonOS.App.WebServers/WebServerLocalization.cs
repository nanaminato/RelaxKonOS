namespace RelaxKonOS.App.WebServers;

/// <summary>Resources used by Web Server Manager and Component Center.</summary>
public static class WebServerLocalization
{
    public static IEnumerable<string> ResourceNames =>
        typeof(WebServerLocalization).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("WebServers.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(WebServerLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing web server localization resource '{resourceName}'.");
}
