namespace RelaxKonOS.ManagedRuntimes;

/// <summary>One GEO artifact shipped with the Server package, named by its fixed installed file name.</summary>
public sealed record MihomoGeoDataFile(string Name, string Sha256);

/// <summary>
/// The closed GEO resource set that ships with the RelaxKonOS package. These digests are the only
/// authority for placing GEO data into a managed runtime: a release package that carries different
/// bytes is refused rather than adopted, and no path, URL, or client-supplied digest can extend the set.
/// </summary>
public static class MihomoGeoDataFiles
{
    public const long MaximumBytes = 128L * 1024 * 1024;

    // SHA-256 of the artifacts committed in RelaxKonOS.Server/Assets/Mihomo/GeoData.
    public static IReadOnlyList<MihomoGeoDataFile> Bundled { get; } =
    [
        new("geoip.metadb", "91ef340938ff44a94ff8e5d8d8bd7e8d7dad9d9e3c4ecea9e160dd95e6a9916b"),
        new("geoip.dat", "0d5d2ba0c5a5c58027fd1347a6afd57c9470799b6bb3cbc274fd4657ed8de382"),
        new("geosite.dat", "665fad6d83e9f3cf28ec7200d2812280508fbbf07983818a33caf90514ab6f17"),
        new("country.mmdb", "fe721d5e47d320b2a23db4eafddb796a22026ef01899bbe7007fc0274016e5f4"),
        new("GeoLite2-ASN.mmdb", "93456017eef970e7e60ab66312402b2130bb233af792a5aa30b2ff4de854c5cf"),
    ];

    public const string PrimaryFile = "geoip.metadb";

    public static bool IsDeclared(string name) => Bundled.Any(file => string.Equals(file.Name, name, StringComparison.Ordinal));

    public static bool DigestMatches(string expected, string actual) => string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
}
