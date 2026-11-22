namespace RelaxKonOS.App.Settings;

/// <summary>Settings resources loaded by the workspace and the desktop host.</summary>
public static class SettingsLocalization
{
    public static IEnumerable<string> ResourceNames =>
        typeof(SettingsLocalization).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("Settings.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(SettingsLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing Settings localization resource '{resourceName}'.");
}
