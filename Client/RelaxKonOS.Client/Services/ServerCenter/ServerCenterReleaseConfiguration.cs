using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.ServerCenter;

/// <summary>
/// 本机固定的发布信任配置。它把「可信发布公钥」「部署工具目录」「发布包目录」三者绑在一起：
/// 客户端永远不信任包内携带的公钥，只信任这里记录并已由用户或发布流程固定的那一份。
/// <para>未配置时不得退化为「信任任何来源」，而是让安装/升级在接触宿主前就停下并说明缺少什么。</para>
/// </summary>
public sealed record ServerCenterReleaseTrust(
    IReadOnlyList<string> TrustedKeyIds,
    IReadOnlyDictionary<string, string> PublicKeys,
    string? ReleaseRoot,
    bool AllowDevelopmentSource)
{
    /// <summary>默认状态：没有可信公钥，也没有部署资产目录。它表示「尚未配置」，不是「允许一切」。</summary>
    public static ServerCenterReleaseTrust Unconfigured { get; } =
        new([], new Dictionary<string, string>(StringComparer.Ordinal), null, false);

    /// <summary>是否已具备执行安装/升级所需的信任根与资产目录。</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ReleaseRoot)
        && TrustedKeyIds.Count > 0
        && TrustedKeyIds.Any(id => PublicKeys.TryGetValue(id, out var pem) && !string.IsNullOrWhiteSpace(pem));

    /// <summary>投影为协议层的信任策略；开发来源例外必须显式开启。</summary>
    public ServerReleaseTrustPolicy ToPolicy() => new([.. TrustedKeyIds], AllowDevelopmentSource);

    /// <summary>取某个 keyId 对应的公钥 PEM；未固定时返回 null。</summary>
    public string? PublicKeyFor(string? keyId) =>
        keyId is not null && PublicKeys.TryGetValue(keyId, out var pem) && !string.IsNullOrWhiteSpace(pem) ? pem : null;
}

/// <summary>发布信任配置的读取接口。它只读本机文件，不与 Workspace 同步。</summary>
public interface IServerCenterReleaseTrustStore
{
    Task<ServerCenterReleaseTrust> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 文件实现：<c>%LocalAppData%/RelaxKonOS/servercenter/release-trust.json</c>。
/// 文件缺失或损坏时返回 <see cref="ServerCenterReleaseTrust.Unconfigured"/>，
/// 使安装/升级停下并提示配置，而不是静默信任一个读不懂的旧文件。
/// </summary>
public sealed class FileServerCenterReleaseTrustStore : IServerCenterReleaseTrustStore
{
    private readonly string _filePath;

    public FileServerCenterReleaseTrustStore(string? directory = null)
    {
        var root = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RelaxKonOS",
            "servercenter");
        _filePath = Path.Combine(root, "release-trust.json");
    }

    public async Task<ServerCenterReleaseTrust> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return ServerCenterReleaseTrust.Unconfigured;

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var payload = await JsonSerializer
                .DeserializeAsync<ReleaseTrustFile>(stream, RelaxKonOSJsonOptions.Default, cancellationToken)
                .ConfigureAwait(false);
            if (payload is null) return ServerCenterReleaseTrust.Unconfigured;

            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (id, pem) in payload.PublicKeys ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(pem)) continue;
                keys[id.Trim()] = pem;
            }

            var ids = (payload.TrustedKeyIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var root = string.IsNullOrWhiteSpace(payload.ReleaseRoot) ? null : payload.ReleaseRoot.Trim();
            return new ServerCenterReleaseTrust(ids, keys, root, payload.AllowDevelopmentSource);
        }
        catch (JsonException)
        {
            return ServerCenterReleaseTrust.Unconfigured;
        }
        catch (IOException)
        {
            return ServerCenterReleaseTrust.Unconfigured;
        }
    }

    private sealed record ReleaseTrustFile(
        IReadOnlyList<string>? TrustedKeyIds,
        IReadOnlyDictionary<string, string>? PublicKeys,
        string? ReleaseRoot,
        bool AllowDevelopmentSource);
}

/// <summary>某个平台的一次部署工具：远端启动器与自包含发布验证器。两者都是可重开的流。</summary>
public sealed record ServerCenterDeploymentTools(
    HostPlatformKind Platform,
    string LauncherFileName,
    string VerifierFileName,
    Func<Stream> OpenLauncher,
    Func<Stream> OpenVerifier);

/// <summary>
/// 一次安装/升级所需的完整签名发布材料。启动器与验证器对所有操作都需要；
/// 签名包、RID 与摘要只在安装/升级时需要。
/// </summary>
public sealed record ServerCenterReleaseAssets(
    ServerCenterDeploymentTools Tools,
    string KeyId,
    string PublicKeyPem,
    ServerReleaseTrustPolicy TrustPolicy,
    ServerRuntimeIdentifier Runtime,
    string Version,
    string StagedPackageName,
    string PackageDigest,
    Func<Stream> OpenSignedArchive);

/// <summary>
/// 部署资产来源。它把「客户端自带的部署工具」与「已固定信任的签名发布包」分开暴露：
/// 预检只需要工具，安装/升级还需要一个通过本地签名验证的包。
/// </summary>
public interface IServerCenterReleaseSource
{
    /// <summary>该平台可用的部署工具；未配置部署资产目录时返回 null。</summary>
    Task<ServerCenterDeploymentTools?> ResolveToolsAsync(
        HostPlatformKind platform, CancellationToken cancellationToken = default);

    /// <summary>该平台、该 RID、该模式对应的签名发布包；缺少可信包时返回 null。</summary>
    Task<ServerCenterReleaseAssets?> ResolveReleaseAsync(
        HostPlatformKind platform,
        ServerRuntimeIdentifier runtime,
        ServerInstallMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 由用户本地选取的离线包生成发布材料。它同样必须通过已固定公钥的签名验证，
    /// 因此「本地包」不是信任例外，只是来源不同。
    /// </summary>
    Task<ServerCenterReleaseAssets?> ResolveLocalBundleAsync(
        HostPlatformKind platform,
        ServerRuntimeIdentifier runtime,
        ServerInstallMode mode,
        string archivePath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 文件来源。它按固定目录布局读取资产，不接受任意路径：
/// <code>
/// &lt;releaseRoot&gt;/launcher/relaxkonos-deploy.sh          Linux 启动器
/// &lt;releaseRoot&gt;/launcher/RelaxKonOS-Deploy.ps1          Windows 启动器
/// &lt;releaseRoot&gt;/launcher/release-verifier               Linux 验证器
/// &lt;releaseRoot&gt;/launcher/release-verifier.exe           Windows 验证器
/// &lt;releaseRoot&gt;/packages/&lt;rid&gt;/*.zip                   该 RID 的签名发布包
/// </code>
/// </summary>
public sealed class FileServerCenterReleaseSource(
    IServerCenterReleaseTrustStore trustStore,
    string? releaseRootOverride = null) : IServerCenterReleaseSource
{
    public async Task<ServerCenterDeploymentTools?> ResolveToolsAsync(
        HostPlatformKind platform, CancellationToken cancellationToken = default)
    {
        var root = await ResolveRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null) return null;

        var (launcherName, verifierName) = platform == HostPlatformKind.Windows
            ? ("RelaxKonOS-Deploy.ps1", "release-verifier.exe")
            : ("relaxkonos-deploy.sh", "release-verifier");

        var launcher = Path.Combine(root, "launcher", launcherName);
        var verifier = Path.Combine(root, "launcher", verifierName);
        if (!File.Exists(launcher) || !File.Exists(verifier)) return null;

        return new ServerCenterDeploymentTools(
            platform, launcherName, verifierName,
            () => File.OpenRead(launcher),
            () => File.OpenRead(verifier));
    }

    public async Task<ServerCenterReleaseAssets?> ResolveReleaseAsync(
        HostPlatformKind platform,
        ServerRuntimeIdentifier runtime,
        ServerInstallMode mode,
        CancellationToken cancellationToken = default)
    {
        var trust = await trustStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!trust.IsConfigured) return null;

        var tools = await ResolveToolsAsync(platform, cancellationToken).ConfigureAwait(false);
        if (tools is null) return null;

        var root = await ResolveRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null) return null;

        var packageDirectory = Path.Combine(root, "packages", RuntimeToken(runtime));
        if (!Directory.Exists(packageDirectory)) return null;

        // One package per RID is the supported layout. Picking the newest by name keeps a stale
        // archive from being chosen silently when an operator drops in a replacement.
        var candidates = Directory.EnumerateFiles(packageDirectory, "*.zip", SearchOption.TopDirectoryOnly)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        var expectedKind = mode == ServerInstallMode.LinuxUser
            ? ServerReleasePackageKind.UserServer : ServerReleasePackageKind.Server;

        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ServerDeploymentInputRules.IsSafeStagedPackageName(Path.GetFileName(path))) continue;

            var assets = await BuildAssetsAsync(trust, tools, runtime, expectedKind, path, cancellationToken)
                .ConfigureAwait(false);
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
        var trust = await trustStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!trust.IsConfigured) return null;

        var tools = await ResolveToolsAsync(platform, cancellationToken).ConfigureAwait(false);
        if (tools is null) return null;

        if (!File.Exists(archivePath) ||
            !ServerDeploymentInputRules.IsSafeStagedPackageName(Path.GetFileName(archivePath)))
            return null;

        var expectedKind = mode == ServerInstallMode.LinuxUser
            ? ServerReleasePackageKind.UserServer : ServerReleasePackageKind.Server;
        return await BuildAssetsAsync(trust, tools, runtime, expectedKind, archivePath, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<ServerCenterReleaseAssets?> BuildAssetsAsync(
        ServerCenterReleaseTrust trust,
        ServerCenterDeploymentTools tools,
        ServerRuntimeIdentifier runtime,
        ServerReleasePackageKind expectedKind,
        string packagePath,
        CancellationToken cancellationToken)
    {
        byte[] digestBytes;
        await using (var stream = File.OpenRead(packagePath))
        {
            digestBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        var digest = Convert.ToHexString(digestBytes).ToLowerInvariant();

        // The client verifies the archive locally before upload, using the pinned key only. Reading the
        // manifest here additionally rejects a package that is signed by a key this client does not trust.
        await using var archive = File.OpenRead(packagePath);
        var manifest = ReadManifest(archive);
        if (manifest is null || manifest.PackageKind != expectedKind || manifest.Runtime != runtime) return null;

        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in trust.TrustedKeyIds)
            if (trust.PublicKeyFor(id) is { } pem) keys[id] = pem;
        if (keys.Count == 0) return null;

        archive.Position = 0;
        var verification = ServerReleaseArchiveVerifier.Verify(
            archive, expectedKind, runtime, keys, digest);
        if (!verification.Verified || verification.Manifest is null) return null;

        var keyId = trust.TrustedKeyIds.First(id => keys.ContainsKey(id));
        var packageName = Path.GetFileName(packagePath);
        return new ServerCenterReleaseAssets(
            tools,
            keyId,
            keys[keyId],
            trust.ToPolicy(),
            runtime,
            verification.Manifest.Version,
            packageName,
            digest,
            () => File.OpenRead(packagePath));
    }

    private static ServerReleaseManifestDto? ReadManifest(Stream archive)
    {
        try
        {
            using var zip = new System.IO.Compression.ZipArchive(archive, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
            var entry = zip.GetEntry("manifest.json");
            if (entry is null || entry.Length > 1024 * 1024) return null;
            using var content = entry.Open();
            return JsonSerializer.Deserialize<ServerReleaseManifestDto>(content, RelaxKonOSJsonOptions.Default);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> ResolveRootAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(releaseRootOverride)) return releaseRootOverride.Trim();
        var trust = await trustStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(trust.ReleaseRoot) ? null : trust.ReleaseRoot;
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
