using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.Protocol.Developer;

namespace RelaxKonOS.Client.Services.Developer;

/// <summary>The desktop distribution supplies this source alongside the client binaries.</summary>
internal sealed partial class OfficialDesktopPackages(string directory, HttpClient? client = null)
{
    public const string OriginFileName = "package-origin.json";
    public string BundledProfilesPath => Path.Combine(directory, "installation-profiles.json");

    public static bool IsReserved(string? appId) => appId?.StartsWith("relaxkonos.", StringComparison.Ordinal) == true;

    internal static IReadOnlyList<string> Ids => OfficialDesktopCatalogRules.ApplicationIds;

    public static bool IsExternalOfficialId(string appId) => Ids.Contains(appId, StringComparer.Ordinal);

    public static bool IsAllowed(string appId, PackageOrigin origin) => IsExternalOfficialId(appId)
        && (origin == PackageOrigin.OfficialDistribution || (origin == PackageOrigin.DirectDeploy && DeveloperProfile.IsDebug));

    internal static bool OwnsSettingsWorkspace(string appId, PackageOrigin origin) =>
        origin == PackageOrigin.OfficialDistribution && appId == "relaxkonos.settings";

    internal static bool OwnsComponentCenterWorkspace(string appId, PackageOrigin origin) =>
        origin == PackageOrigin.OfficialDistribution && appId == "relaxkonos.component-center";

    internal static bool OwnsExplorerWorkspace(string appId, PackageOrigin origin) =>
        origin == PackageOrigin.OfficialDistribution && appId == "relaxkonos.explorer";

    internal static bool OwnsServerCenterWorkspace(string appId, PackageOrigin origin) =>
        origin == PackageOrigin.OfficialDistribution && appId == "relaxkonos.server-center";

    internal static bool OwnsShellRoute(string appId, PackageOrigin origin, Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme.Equals("relaxkonos", StringComparison.OrdinalIgnoreCase)
        && (OwnsSettingsWorkspace(appId, origin) && uri.Host.Equals("settings", StringComparison.OrdinalIgnoreCase)
            || OwnsComponentCenterWorkspace(appId, origin)
                && Apps.ComponentCenter.ComponentCenterNavigation.TryParse(uri, out _)
            || OwnsExplorerWorkspace(appId, origin) && RelaxKonOS.AppSDK.RelaxKonOSActivationUris.TryReadExplorerPath(uri, out _));

    public async Task<string> VerifyAsync(string path, string appId, Stream package, CancellationToken cancellationToken)
    {
        var expectedPath = Path.Combine(directory, appId + ".roapp");
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!IsExternalOfficialId(appId) || !Path.GetFullPath(path).Equals(expectedPath, pathComparison))
            throw new InvalidOperationException("The relaxkonos.* application id range requires the preconfigured OfficialApps source.");

        var checksumPath = expectedPath + ".sha256";
        if (!File.Exists(checksumPath))
            throw new InvalidOperationException("The official package checksum is missing.");
        if (new FileInfo(checksumPath).Length > 128
            || new[] { directory, expectedPath, checksumPath }.Any(candidate =>
                (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidOperationException("The official source contains an invalid package or checksum path.");
        var sha256 = (await File.ReadAllTextAsync(checksumPath, cancellationToken)).Trim().ToLowerInvariant();
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("The official package checksum is invalid.");
        package.Position = 0;
        await AppPackageInstallerService.VerifyFrozenAsync(package, sha256, cancellationToken);
        return sha256;
    }
}

internal enum PackageOrigin
{
    LocalPackage,
    OfficialDistribution,
    DirectDeploy,
}
