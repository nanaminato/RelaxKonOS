namespace RelaxKonOS.App.Firewall;

public static class FirewallLocalization
{
    public static IEnumerable<string> ResourceNames => typeof(FirewallLocalization).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("Firewall.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(FirewallLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing Firewall localization resource '{resourceName}'.");
}
