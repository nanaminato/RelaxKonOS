namespace RelaxKonOS.App.Git;

public static class GitLocalization
{
    public static IEnumerable<string> ResourceNames => typeof(GitLocalization).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("Git.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(GitLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing Git localization resource '{resourceName}'.");
}
