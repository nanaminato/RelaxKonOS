using System.Security.Cryptography;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.ServerCenter;

/// <summary>可重新打开的远端启动器和包检查工具。</summary>
public sealed record ServerCenterDeploymentTools(
    HostPlatformKind Platform,
    string LauncherFileName,
    string VerifierFileName,
    Func<Stream> OpenLauncher,
    Func<Stream> OpenVerifier);

/// <summary>一次安装或升级所需的发布包、摘要和部署工具。</summary>
public sealed record ServerCenterReleaseAssets(
    ServerCenterDeploymentTools Tools,
    ServerRuntimeIdentifier Runtime,
    string Version,
    string StagedPackageName,
    string PackageDigest,
    Func<Stream> OpenArchive);

public interface IServerCenterReleaseSource
{
    Task<ServerCenterDeploymentTools?> ResolveToolsAsync(
        HostPlatformKind platform, CancellationToken cancellationToken = default);

    Task<ServerCenterReleaseAssets?> ResolveReleaseAsync(
        HostPlatformKind platform,
        ServerRuntimeIdentifier runtime,
        ServerInstallMode mode,
        CancellationToken cancellationToken = default);

    Task<ServerCenterReleaseAssets?> ResolveLocalBundleAsync(
        HostPlatformKind platform,
        ServerRuntimeIdentifier runtime,
        ServerInstallMode mode,
        string archivePath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 读取客户端旁的 launcher/ 和发布 ZIP。开发或自行打包时可通过
/// RELAXKONOS_RELEASE_ROOT 指向打包产物目录，无需配置发布公钥。
/// </summary>
public sealed class FileServerCenterReleaseSource(string? releaseRootOverride = null) : IServerCenterReleaseSource
{
    public Task<ServerCenterDeploymentTools?> ResolveToolsAsync(
        HostPlatformKind platform, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = ResolveRoot();
        if (!Directory.Exists(root)) return Task.FromResult<ServerCenterDeploymentTools?>(null);
        var (launcherName, verifierName) = platform == HostPlatformKind.Windows
            ? ("RelaxKonOS-Deploy.ps1", "release-verifier.exe")
            : ("relaxkonos-deploy.sh", "release-verifier");
        var launcher = Path.Combine(root, "launcher", launcherName);
        var verifier = Path.Combine(root, "launcher", verifierName);
        if (!File.Exists(launcher) || !File.Exists(verifier))
            return Task.FromResult<ServerCenterDeploymentTools?>(null);

        return Task.FromResult<ServerCenterDeploymentTools?>(new(
            platform, launcherName, verifierName,
            () => File.OpenRead(launcher),
            () => File.OpenRead(verifier)));
    }

    public async Task<ServerCenterReleaseAssets?> ResolveReleaseAsync(
        HostPlatformKind platform,
        ServerRuntimeIdentifier runtime,
        ServerInstallMode mode,
        CancellationToken cancellationToken = default)
    {
        var tools = await ResolveToolsAsync(platform, cancellationToken).ConfigureAwait(false);
        if (tools is null) return null;
        var root = ResolveRoot();
        var kind = mode == ServerInstallMode.LinuxUser
            ? ServerReleasePackageKind.UserServer : ServerReleasePackageKind.Server;
        var token = RuntimeToken(runtime);
        var kindToken = kind == ServerReleasePackageKind.UserServer ? "user-server" : "server";
        var candidates = Directory.EnumerateFiles(root, $"RelaxKonOS-*-{token}-{kindToken}.zip", SearchOption.TopDirectoryOnly)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal);
        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assets = await BuildAssetsAsync(tools, runtime, kind, path, cancellationToken).ConfigureAwait(false);
            if (assets is not null) return assets;
        }
        return null;
    }

    public async Task<ServerCenterReleaseAssets?> ResolveLocalBundleAsync(
        HostPlatformKind platform,
        ServerRuntimeIdentifier runtime,
        ServerInstallMode mode,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var tools = await ResolveToolsAsync(platform, cancellationToken).ConfigureAwait(false);
        if (tools is null || !File.Exists(archivePath)) return null;
        var kind = mode == ServerInstallMode.LinuxUser
            ? ServerReleasePackageKind.UserServer : ServerReleasePackageKind.Server;
        return await BuildAssetsAsync(tools, runtime, kind, archivePath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ServerCenterReleaseAssets?> BuildAssetsAsync(
        ServerCenterDeploymentTools tools,
        ServerRuntimeIdentifier runtime,
        ServerReleasePackageKind kind,
        string packagePath,
        CancellationToken cancellationToken)
    {
        var packageName = Path.GetFileName(packagePath);
        if (!ServerDeploymentInputRules.IsSafeStagedPackageName(packageName)) return null;
        await using var archive = File.OpenRead(packagePath);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(archive, cancellationToken)).ToLowerInvariant();
        archive.Position = 0;
        var verification = ServerReleaseArchiveVerifier.Verify(archive, kind, runtime, digest);
        if (!verification.Verified || verification.Manifest is null) return null;
        return new ServerCenterReleaseAssets(
            tools, runtime, verification.Manifest.Version, packageName, digest,
            () => File.OpenRead(packagePath));
    }

    private string ResolveRoot()
    {
        var configured = releaseRootOverride ?? Environment.GetEnvironmentVariable("RELAXKONOS_RELEASE_ROOT");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? AppContext.BaseDirectory : configured);
    }

    private static string RuntimeToken(ServerRuntimeIdentifier runtime) => runtime switch
    {
        ServerRuntimeIdentifier.WinX64 => "win-x64",
        ServerRuntimeIdentifier.WinArm64 => "win-arm64",
        ServerRuntimeIdentifier.LinuxX64 => "linux-x64",
        ServerRuntimeIdentifier.LinuxArm64 => "linux-arm64",
        _ => throw new ArgumentOutOfRangeException(nameof(runtime), runtime, null)
    };
}
