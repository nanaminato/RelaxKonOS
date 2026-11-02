using RelaxKonOS.Client.Apps.Explorer;
using System.IO.Compression;
using RelaxKonOS.Client.Services.Developer;
using System.Security.Cryptography;
using RelaxKonOS.ManagedRuntimes.Catalog;

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

    public AppPackageInstallerService(DeveloperPackageManager packages, IExplorerClient files, ComponentCatalogSourceStore catalogSources)
    {
        _packages = packages;
        _files = files;
        _catalogSources = catalogSources;
    }

    public async Task<AppPackageCandidate> InspectLocalAsync(string localPath, string? displayName = null, bool deleteWhenFinished = false,
        CancellationToken cancellationToken = default)
    {
        var manifest = await _packages.InspectAsync(localPath, cancellationToken);
        var installed = _packages.FindInstalled(manifest.Id);
        return new AppPackageCandidate(
            localPath,
            displayName ?? Path.GetFileName(localPath),
            manifest,
            installed?.Version,
            deleteWhenFinished,
            await ReadIconAsync(localPath, manifest.IconPath, cancellationToken));
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
        var download = await _files.DownloadAsync(serverPath, cancellationToken)
            ?? throw new FileNotFoundException("The selected server package no longer exists.", serverPath);
        var tempRoot = Path.Combine(Path.GetTempPath(), "RelaxKonOS", "app-installer");
        Directory.CreateDirectory(tempRoot);
        var temporaryPath = Path.Combine(tempRoot, $"{Guid.NewGuid():N}.roapp");
        try
        {
            await using (download.Stream)
            await using (var destination = File.Create(temporaryPath))
                await download.Stream.CopyToAsync(destination, cancellationToken);
            return await InspectLocalAsync(temporaryPath, Path.GetFileName(serverPath), deleteWhenFinished: true,
                cancellationToken: cancellationToken);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    public async Task<DeveloperAppInfo> InstallAsync(AppPackageCandidate candidate, CancellationToken cancellationToken = default)
    {
        await _installLock.WaitAsync(cancellationToken);
        try
        {
            using var authorization = candidate.CatalogAuthorization is { } frozen ? _catalogSources.AcquireInstallLease(frozen) : null;
            DeveloperAppInfo installed;
            await using (var package = new FileStream(candidate.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await VerifyFrozenAsync(package, candidate.ExpectedSha256, cancellationToken);
                if (candidate.ExpectedSha256 is not null && _packages.FindInstalled(candidate.Manifest.Id)?.Version != candidate.InstalledVersion)
                    throw new InvalidOperationException("component.state_revision_conflict");
                authorization?.Revalidate();
                installed = await _packages.InstallAsync(package, launch: false, cancellationToken);
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
    public bool IsUpdate => InstalledVersion is not null && InstalledVersion != Manifest.Version.Trim();
    public bool IsRepair => InstalledVersion is not null && InstalledVersion == Manifest.Version.Trim();
}
