namespace RelaxKonOS.App.ComponentCenter;

/// <summary>ComponentCenter resources loaded by the workspace and the desktop host.</summary>
public static class ComponentCenterLocalization
{
    public static IEnumerable<string> ResourceNames =>
        typeof(ComponentCenterLocalization).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("ComponentCenter.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(ComponentCenterLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing ComponentCenter localization resource '{resourceName}'.");
}
