using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.ComponentCenter;
using System.IO.Compression;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Deployment;
using System.Security.Cryptography;
using RelaxKonOS.ManagedRuntimes.Catalog;
using RelaxKonOS.Protocol.Developer;

namespace RelaxKonOS.Client.Services.AppPackages;

/// <summary>
/// User-facing package installation service. Remote packages are copied to a private local
/// staging file before their manifest is inspected or the package is installed.
/// </summary>
public sealed class AppPackageInstallerService
{
    private readonly DeveloperPackageManager _packages;
    private readonly IExplorerClient _files;
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private readonly ComponentCatalogSourceStore _catalogSources;
    private readonly IAuthSession? _session;

    public AppPackageInstallerService(DeveloperPackageManager packages, IExplorerClient files, ComponentCatalogSourceStore catalogSources,
        IAuthSession? session = null)
    {
        _packages = packages;
        _files = files;
        _catalogSources = catalogSources;
        _session = session;
    }

    public async Task<AppPackageCandidate> InspectLocalAsync(string localPath, string? displayName = null, bool deleteWhenFinished = false,
        CancellationToken cancellationToken = default)
    {
        var (manifest, officialSha256) = await _packages.InspectAsync(localPath, cancellationToken);
        var installed = _packages.FindInstalled(manifest.Id);
        return new AppPackageCandidate(
            localPath,
            displayName ?? (OfficialDesktopPackages.IsReserved(manifest.Id) ? "OfficialApps / " + Path.GetFileName(localPath) : Path.GetFileName(localPath)),
            manifest,
            installed?.Version,
            deleteWhenFinished,
            await ReadIconAsync(localPath, manifest.IconPath, cancellationToken),
            ExpectedSha256: officialSha256);
    }

    private static async Task<byte[]?> ReadIconAsync(string packagePath, string? iconPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(iconPath)) return null;
        using var archive = ZipFile.OpenRead(packagePath);
        var entry = archive.GetEntry(iconPath.Replace('\\', '/'));
        if (entry is null || entry.Length is <= 0 or > 2 * 1024 * 1024) return null;
        await using var stream = entry.Open();
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, ct);
        return output.ToArray();
    }

    public async Task<AppPackageCandidate> StageServerPackageAsync(string serverPath, CancellationToken cancellationToken = default)
    {
        if (_session?.State != AuthSessionState.Authenticated || _session.CurrentUser is not { } actor
            || _session.ServiceId is not { } connection || _session.CurrentSession is not { } login)
            throw new InvalidOperationException("deployment-account-changed");
        var origin = new DeploymentServerArchive(actor.Id, connection, serverPath);
        bool IsCurrent() => ReferenceEquals(login, _session.CurrentSession) && MatchesServerArchive(_session, origin);
        var download = await _files.DownloadAsync(serverPath, cancellationToken, IsCurrent)
            ?? throw new FileNotFoundException("The selected server package no longer exists.", serverPath);
        await using var stream = download.Stream;
        var temporaryPath = NewStagingPath();
        try
        {
            await using (var destination = File.Create(temporaryPath))
                await stream.CopyToAsync(destination, cancellationToken);
            var candidate = await InspectLocalAsync(temporaryPath, Path.GetFileName(serverPath), deleteWhenFinished: true,
                cancellationToken: cancellationToken);
            if (!IsCurrent())
                throw new InvalidOperationException("deployment-account-changed");
            return candidate with { ServerArchive = origin };
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    internal static bool MatchesServerArchive(IAuthSession? session, DeploymentServerArchive archive) =>
        session?.State == AuthSessionState.Authenticated && session.CurrentUser?.Id == archive.AccountId
        && session.ServiceId == archive.ConnectionIdentity;

    public Task<DeveloperAppInfo> InstallAsync(AppPackageCandidate candidate, CancellationToken cancellationToken = default) =>
        InstallAsync(candidate, cancellationToken, null);

    internal async Task<DeveloperAppInfo> InstallAsync(AppPackageCandidate candidate, CancellationToken cancellationToken,
        Func<DeveloperPackageManifest, CancellationToken, Task<Guid>>? verifyDeployment)
    {
        await _installLock.WaitAsync(cancellationToken);
        try
        {
            if (candidate.ServerArchive is { } archive && !MatchesServerArchive(_session, archive))
                throw new InvalidOperationException("deployment-account-changed");
            using var authorization = candidate.CatalogAuthorization is { } frozen ? _catalogSources.AcquireInstallLease(frozen) : null;
            DeveloperAppInfo installed;
            await using (var package = new FileStream(candidate.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (OfficialDesktopPackages.IsReserved(candidate.Manifest.Id) && candidate.ExpectedSha256 is { } officialSha256)
                {
                    installed = await _packages.InstallOfficialAsync(package, candidate.LocalPath, candidate.Manifest.Id, officialSha256,
                        candidate.InstalledVersion, cancellationToken, candidate.OfficialRelease, candidate.ExpectedInstalledPath,
                        verifyDeployment);
                }
                else
                {
                    await VerifyFrozenAsync(package, candidate.ExpectedSha256, cancellationToken);
                    if (candidate.ExpectedSha256 is not null && _packages.FindInstalled(candidate.Manifest.Id)?.Version != candidate.InstalledVersion)
                        throw new InvalidOperationException("component.state_revision_conflict");
                    authorization?.Revalidate();
                    installed = await _packages.InstallAsync(package, launch: false, cancellationToken, verifyDeployment);
                }
            }
            // Dispose before deleting a staged file on Windows.
            if (candidate.DeleteWhenFinished && File.Exists(candidate.LocalPath)) File.Delete(candidate.LocalPath);
            return installed;
        }
        finally { _installLock.Release(); }
    }

    public static async Task VerifyFrozenAsync(Stream package, string? expectedSha256, CancellationToken ct)
    {
        if (expectedSha256 is null) return;
        if (!package.CanSeek || !Convert.ToHexStringLower(await SHA256.HashDataAsync(package, ct)).Equals(expectedSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("component.package_expectation_changed");
        package.Position = 0; // The domain installer consumes the very same verified handle.
    }

    public void Discard(AppPackageCandidate candidate)
    {
        if (candidate.DeleteWhenFinished && File.Exists(candidate.LocalPath))
            File.Delete(candidate.LocalPath);
    }

    private static string NewStagingPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "RelaxKonOS", "app-installer");
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"{Guid.NewGuid():N}.roapp");
    }

    internal async Task<OfficialDesktopCatalog> BrowseOfficialAsync(CancellationToken ct)
    {
        var releases = new List<OfficialDesktopRelease>();
        var unavailable = false;
        try { releases.AddRange(await _packages.OfficialSource.BrowseOnlineAsync(ct)); }
        catch (Exception error) when (!ct.IsCancellationRequested && IsStoreFailure(error)) { unavailable = true; }
        foreach (var id in OfficialDesktopPackages.Ids)
        {
            var path = _packages.OfficialSource.BundledPath(id);
            if (!File.Exists(path)) continue;
            try
            {
                var bytes = new FileInfo(path).Length;
                if (bytes is <= 0 or > OfficialDesktopCatalogRules.MaximumPackageBytes)
                    throw new InvalidDataException("Invalid bundled desktop package size.");
                var (manifest, sha256) = await _packages.InspectAsync(path, ct);
                if (manifest.Id != id || sha256 is null || !OfficialDesktopCatalogRules.ValidVersion(manifest.Version))
                    throw new InvalidDataException("Invalid bundled desktop release.");
                var online = releases.SingleOrDefault(release => release.AppId == id && release.Version == manifest.Version);
                if (online is not null)
                {
                    releases.Remove(online);
                    unavailable |= online.Sha256 != sha256 || online.Bytes != bytes;
                }
                releases.Add(new(id, manifest.Version, manifest.DisplayName, manifest.Description, sha256, bytes, null));
            }
            catch (Exception error) when (!ct.IsCancellationRequested && IsStoreFailure(error)) { unavailable = true; }
        }
        return new(releases, unavailable);
    }

    internal async Task<AppPackageCandidate> StageOfficialAsync(OfficialDesktopRelease release, CancellationToken ct)
    {
        var installed = _packages.FindInstalled(release.AppId);
        if (!CanInstallRelease(release, installed))
            throw new InvalidOperationException("Choose the installed version or a newer official desktop release.");
        if (release.CatalogUri is null)
        {
            var candidate = await InspectLocalAsync(_packages.OfficialSource.BundledPath(release.AppId), cancellationToken: ct);
            if (candidate.ExpectedSha256 != release.Sha256 || candidate.Manifest.Version != release.Version
                || candidate.InstalledVersion != installed?.Version)
                throw new InvalidOperationException("component.package_expectation_changed");
            return candidate with { OfficialRelease = release, ExpectedInstalledPath = installed?.InstallationPath };
        }

        var path = NewStagingPath();
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await _packages.OfficialSource.DownloadAsync(release, output, ct);
            var manifest = await _packages.InspectOfficialDownloadAsync(path, release, ct);
            return new AppPackageCandidate(path, release.CatalogUri.GetLeftPart(UriPartial.Authority), manifest, installed?.Version,
                DeleteWhenFinished: true, IconData: await ReadIconAsync(path, manifest.IconPath, ct), ExpectedSha256: release.Sha256)
            {
                OfficialRelease = release,
                ExpectedInstalledPath = installed?.InstallationPath,
            };
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    internal async Task<AppPackageCandidate?> RecoverComponentArchiveAsync(DeploymentComponentRelease reviewed, string digest, CancellationToken ct)
    {
        var source = new HttpCatalogSource(_catalogSources, reviewed.SourceId);
        source.Load();
        var authorization = source.Authorize(reviewed.ComponentId, RoappComponentInstallerProvider.Desktop);
        if (!MatchesReviewedComponentRelease(reviewed, digest, authorization)) return null;
        var plan = await new RoappComponentInstallerProvider(this).PlanAsync(source, reviewed.ComponentId, ct);
        if (plan.Package.CatalogAuthorization is { } current && MatchesReviewedComponentRelease(reviewed, digest, current))
            return plan.Package;

        Discard(plan.Package);
        return null;
    }

    internal void RevalidateComponentAuthorization(AppPackageCandidate candidate)
    {
        if (candidate.CatalogAuthorization is not { } authorization) return;
        using var lease = _catalogSources.AcquireInstallLease(authorization);
        lease.Revalidate();
    }

    internal static bool MatchesReviewedComponentRelease(DeploymentComponentRelease reviewed, string digest,
        ComponentCatalogInstallAuthorization authorization) =>
        authorization.SourceId == reviewed.SourceId && authorization.PublisherId == reviewed.PublisherId
        && authorization.ReleaseIdentity == reviewed.ReleaseIdentity && authorization.ArtifactSha256 == digest;

    internal IReadOnlyList<DeveloperAppInfo> InstalledOfficial =>
        _packages.Installed.Where(app => OfficialDesktopPackages.IsExternalOfficialId(app.Id)).ToArray();

    internal Task<bool> UninstallOfficialAsync(DeveloperAppInfo reviewed) => _packages.UninstallOfficialAsync(reviewed);

    internal static bool CanInstallRelease(OfficialDesktopRelease release, DeveloperAppInfo? installed) => installed is null
        || release.Version == installed.Version
        || Version.TryParse(installed.Version, out var current) && Version.Parse(release.Version) > current;

    internal static bool IsStoreFailure(Exception error) => error is IOException or InvalidDataException or InvalidOperationException
        or System.Text.Json.JsonException or HttpRequestException or UnauthorizedAccessException or OperationCanceledException;
}

public sealed record AppPackageCandidate(
    string LocalPath,
    string SourceName,
    DeveloperPackageManifest Manifest,
    string? InstalledVersion,
    bool DeleteWhenFinished,
    byte[]? IconData = null, string? ExpectedSha256 = null, string? CatalogSourceId = null,
    ComponentCatalogInstallAuthorization? CatalogAuthorization = null)
{
    internal OfficialDesktopRelease? OfficialRelease { get; init; }
    internal string? ExpectedInstalledPath { get; init; }
    internal string? CatalogComponentId { get; init; }
    internal DeploymentServerArchive? ServerArchive { get; init; }
    internal bool IsRecoveredDeployment { get; init; }
    public bool IsUpdate => InstalledVersion is not null && InstalledVersion != Manifest.Version.Trim();
    public bool IsRepair => InstalledVersion is not null && InstalledVersion == Manifest.Version.Trim();
}
