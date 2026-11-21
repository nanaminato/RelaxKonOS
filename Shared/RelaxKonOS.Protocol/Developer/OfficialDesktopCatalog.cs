namespace RelaxKonOS.Protocol.Developer;

public sealed record OfficialDesktopCatalogDocument(int SchemaVersion, OfficialDesktopCatalogEntry[] Releases);

public sealed record OfficialDesktopCatalogEntry(string AppId, string Version, string DisplayName, string? Description,
    string Sha256, long Bytes);

public static class OfficialDesktopCatalogRules
{
    public const int SchemaVersion = 1;
    public const int MaximumReleases = 128;
    public const int MaximumCatalogBytes = 128 * 1024;
    public const long MaximumPackageBytes = 128 * 1024 * 1024;
    public static IReadOnlyList<string> ApplicationIds { get; } = Array.AsReadOnly(new[]
    {
        "relaxkonos.application-deployments", "relaxkonos.browser", "relaxkonos.certificates", "relaxkonos.codeeditor", "relaxkonos.docker", "relaxkonos.event-alerts", "relaxkonos.file-services", "relaxkonos.firewall", "relaxkonos.git", "relaxkonos.imageviewer", "relaxkonos.notepad", "relaxkonos.port-forwarding", "relaxkonos.processguardian", "relaxkonos.registry", "relaxkonos.taskmanager", "relaxkonos.terminal", "relaxkonos.tunnels", "relaxkonos.webservers", "relaxkonos.welcome"
    });

    public static bool ValidVersion(string? value) => Version.TryParse(value, out var version)
        && version.Build >= 0 && version.Revision == -1 && version.ToString(3) == value;

    public static bool IsValid(OfficialDesktopCatalogEntry? entry) =>
        entry is not null && ApplicationIds.Contains(entry.AppId, StringComparer.Ordinal) && ValidVersion(entry.Version)
        && entry.DisplayName is { Length: > 0 and <= 100 } && !entry.DisplayName.Any(char.IsControl)
        && entry.Description is not { Length: > 1024 } && entry.Description?.Any(char.IsControl) != true
        && entry.Sha256 is { Length: 64 } && entry.Sha256.All(char.IsAsciiHexDigitLower)
        && entry.Bytes is > 0 and <= MaximumPackageBytes;

    public static string ArtifactPath(string id, string version) => id + "/" + version + ".roapp";
}
