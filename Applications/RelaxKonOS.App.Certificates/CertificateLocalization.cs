namespace RelaxKonOS.App.Certificates;

/// <summary>Certificate Manager resources shared with the desktop host's HTTPS settings.</summary>
public static class CertificateLocalization
{
    public static IEnumerable<string> ResourceNames =>
        typeof(CertificateLocalization).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("Certificates.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(CertificateLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing certificate localization resource '{resourceName}'.");
}
