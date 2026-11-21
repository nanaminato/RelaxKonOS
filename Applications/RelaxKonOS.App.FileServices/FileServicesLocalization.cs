namespace RelaxKonOS.App.FileServices;

public static class FileServicesLocalization
{
    public static IEnumerable<string> ResourceNames => typeof(FileServicesLocalization).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("FileServices.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(FileServicesLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing File Services localization resource '{resourceName}'.");
}
