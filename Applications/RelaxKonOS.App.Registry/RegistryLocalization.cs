namespace RelaxKonOS.App.Registry;

public static class RegistryLocalization
{
    public static IEnumerable<string> ResourceNames => typeof(RegistryLocalization).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("Registry.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(RegistryLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing Registry localization resource '{resourceName}'.");
}
