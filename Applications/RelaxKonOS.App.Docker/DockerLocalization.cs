namespace RelaxKonOS.App.Docker;

public static class DockerLocalization
{
    public static IEnumerable<string> ResourceNames => typeof(DockerLocalization).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("Docker.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(DockerLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing Docker localization resource '{resourceName}'.");
}
