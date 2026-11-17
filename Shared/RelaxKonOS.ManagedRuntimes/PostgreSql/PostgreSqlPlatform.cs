using System.Text.RegularExpressions;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;

namespace RelaxKonOS.ManagedRuntimes.PostgreSql;

public sealed class PostgreSqlPlatform
{
    private const string UbuntuSigningKeys =
        "/usr/share/keyrings/ubuntu-archive-keyring.gpg,F6ECB3762474EDA9D21B7022871920D1991BC93C";
    private const string DebianSigningKeys =
        "/usr/share/keyrings/debian-archive-trixie-automatic.gpg,/usr/share/keyrings/debian-archive-trixie-stable.gpg";
    private const string DebianSecurityKey = "/usr/share/keyrings/debian-archive-trixie-security-automatic.gpg";

    private readonly string versionPattern;
    private readonly string minimumVersion;
    private readonly string commonVersionPattern;

    private PostgreSqlPlatform(string distribution, string codename, int major,
        string versionPattern, string minimumVersion, string commonVersionPattern)
    {
        Distribution = distribution;
        Codename = codename;
        Major = major;
        this.versionPattern = versionPattern;
        this.minimumVersion = minimumVersion;
        this.commonVersionPattern = commonVersionPattern;
    }

    public static PostgreSqlPlatform Noble { get; } = new("ubuntu", "noble", 16,
        @"16\.[0-9]+-0ubuntu0\.24\.04\.[0-9]+", "16.15-0ubuntu0.24.04.1", @"257build1(?:\.[0-9]+)?");
    public static PostgreSqlPlatform Resolute { get; } = new("ubuntu", "resolute", 18,
        @"18\.[0-9]+-0ubuntu0\.26\.04\.[0-9]+", "18.6-0ubuntu0.26.04.1", @"[0-9]+ubuntu[0-9]+(?:\.[0-9]+)?");
    public static PostgreSqlPlatform Trixie { get; } = new("debian", "trixie", 17,
        @"17\.[0-9]+-[0-9]+\+deb13u[0-9]+", "17.11-0+deb13u1", @"[0-9]+(?:\+deb13u[0-9]+)?");

    public string Distribution { get; }
    public string Codename { get; }
    public int Major { get; }
    public string Package => $"postgresql-{Major}";
    public string ClientPackage => $"postgresql-client-{Major}";

    public IReadOnlyList<string> GetAptSources(string policy)
    {
        var sources = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(policy,
            @"(?m)^[ \t]*\d+[ \t]+(https?://[^\s]+)[ \t]+([a-z0-9-]+)/main[ \t]+amd64[ \t]+Packages[ \t]*\r?$(?:\n[ \t]+release[ \t]+([^\r\n]+))?"))
        {
            var uri = match.Groups[1].Value;
            var suite = match.Groups[2].Value;
            if (Distribution == "debian" && suite is "stable" or "stable-updates" or "stable-security")
            {
                var codename = Codename + suite["stable".Length..];
                if (!match.Groups[3].Value.Split(',').Contains("n=" + codename, StringComparer.Ordinal)) continue;
                suite = codename;
            }
            if (RepositorySigningKeys(uri, suite) is { } keys)
                sources.Add($"deb [arch=amd64 signed-by={keys}] {uri.TrimEnd('/')} {suite} main");
        }
        return sources.ToArray();
    }

    public static PostgreSqlPlatform? Match(string distribution, string release) => (distribution, release) switch
    {
        ("ubuntu", "24.04") => Noble,
        ("ubuntu", "26.04") => Resolute,
        ("debian", "13") => Trixie,
        _ => null
    };

    public static PostgreSqlPlatform? FromOsRelease(IEnumerable<string> lines)
    {
        var entries = lines.Select(line => line.Split('=', 2)).Where(parts => parts.Length == 2).ToArray();
        var ids = entries.Where(parts => parts[0] == "ID").Select(parts => parts[1].Trim('"', '\'')).ToArray();
        var releases = entries.Where(parts => parts[0] == "VERSION_ID").Select(parts => parts[1].Trim('"', '\'')).ToArray();
        return ids.Length == 1 && releases.Length == 1 ? Match(ids[0], releases[0]) : null;
    }

    public bool SupportsPackageVersion(string? version) => version is { Length: <= 128 }
        && Regex.IsMatch(version, @"\A" + versionPattern + @"\z", RegexOptions.CultureInvariant)
        && DebianVersion.Compare(version, minimumVersion) >= 0;

    internal bool IsCommonPackage(string file, string package) => Regex.IsMatch(file,
        @"\A" + Regex.Escape(package) + "_" + commonVersionPattern + @"_all\.deb\z", RegexOptions.CultureInvariant);

    public string? RepositorySigningKeys(string value, string suite)
    {
        if (!OfficialUri(value, artifact: false, out var root)) return null;
        if (Distribution == "ubuntu")
            return suite == Codename || suite == Codename + "-updates" || suite == Codename + "-security"
                ? UbuntuSigningKeys : null;
        if (root == "/debian-security") return suite == "trixie-security" ? DebianSecurityKey : null;
        return suite is "trixie" or "trixie-updates" ? DebianSigningKeys : null;
    }

    internal bool OfficialArtifact(string value) => OfficialUri(value, artifact: true, out _);

    private bool OfficialUri(string value, bool artifact, out string root)
    {
        root = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || Regex.IsMatch(value, @"%(?!2[bB]|3[aA])", RegexOptions.CultureInvariant)
            || value.Contains('\\') || value.Contains("/../", StringComparison.Ordinal)) return false;
        root = (Distribution, uri.Host) switch
        {
            ("ubuntu", "archive.ubuntu.com" or "security.ubuntu.com" or "cn.archive.ubuntu.com") => "/ubuntu",
            ("debian", "deb.debian.org") when uri.AbsolutePath.StartsWith("/debian-security", StringComparison.Ordinal) => "/debian-security",
            ("debian", "deb.debian.org" or "ftp.debian.org") => "/debian",
            ("debian", "security.debian.org") => "/debian-security",
            _ => ""
        };
        return root.Length != 0 && (artifact
            ? uri.AbsolutePath.StartsWith(root + "/pool/", StringComparison.Ordinal)
                && uri.AbsolutePath.EndsWith(".deb", StringComparison.Ordinal)
            : uri.AbsolutePath.TrimEnd('/') == root);
    }
}
