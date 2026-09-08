using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Services.Developer;

namespace RelaxKonOS.Client.Services.AppPackages;

/// <summary>
/// User-facing package installation service. Remote packages are copied to a private local
/// staging file before their manifest is inspected or the package is installed.
/// </summary>
public sealed class AppPackageInstallerService
{
    private readonly DeveloperPackageManager _packages;
    private readonly IExplorerClient _files;

    public AppPackageInstallerService(DeveloperPackageManager packages, IExplorerClient files)
    {
        _packages = packages;
        _files = files;
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
            deleteWhenFinished);
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
        DeveloperAppInfo installed;
        await using (var package = File.OpenRead(candidate.LocalPath))
            installed = await _packages.InstallAsync(package, launch: false, cancellationToken);

        // The stream must be disposed before Windows permits deleting the staged server package.
        if (candidate.DeleteWhenFinished && File.Exists(candidate.LocalPath))
            File.Delete(candidate.LocalPath);
        return installed;
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
    bool DeleteWhenFinished)
{
    public bool IsUpdate => InstalledVersion is not null;
}
