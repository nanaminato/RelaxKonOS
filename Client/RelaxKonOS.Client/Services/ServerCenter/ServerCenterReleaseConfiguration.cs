using System.Text;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.ServerCenter;

/// <summary>Client-owned deployment script, embedded in every build.</summary>
public sealed record ServerCenterDeploymentTools(
    HostPlatformKind Platform, string LauncherFileName, Func<Stream> OpenLauncher);

public sealed record ServerCenterReleaseAssets(
    ServerCenterDeploymentTools Tools, ServerRuntimeIdentifier Runtime,
    string StagedPackageName, Func<Stream> OpenArchive);

public interface IServerCenterReleaseSource
{
    Task<ServerCenterDeploymentTools?> ResolveToolsAsync(
        HostPlatformKind platform, CancellationToken cancellationToken = default);
    Task<ServerCenterReleaseAssets?> ResolveLocalBundleAsync(
        HostPlatformKind platform, ServerRuntimeIdentifier runtime, ServerInstallMode mode,
        string archivePath, CancellationToken cancellationToken = default);
}

/// <summary>User ZIPs need no official checksum; the host checks layout before installation.</summary>
public sealed class FileServerCenterReleaseSource : IServerCenterReleaseSource
{
    public Task<ServerCenterDeploymentTools?> ResolveToolsAsync(
        HostPlatformKind platform, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = platform == HostPlatformKind.Windows ? "RelaxKonOS-Deploy.ps1" : "relaxkonos-deploy.sh";
        return Task.FromResult<ServerCenterDeploymentTools?>(new(platform, name, () =>
        {
            using var source = typeof(FileServerCenterReleaseSource).Assembly.GetManifestResourceStream(
                "RelaxKonOS.Deployment." + name) ?? throw new InvalidOperationException("The embedded installation script is missing.");
            using var reader = new StreamReader(source, Encoding.UTF8);
            var text = reader.ReadToEnd().Replace("\r\n", "\n");
            // Windows PowerShell 5.1 needs a BOM to recognize UTF-8 scripts.
            var bytes = Encoding.UTF8.GetBytes(platform == HostPlatformKind.Windows ? "\uFEFF" + text : text);
            return new MemoryStream(bytes, writable: false);
        }));
    }

    public async Task<ServerCenterReleaseAssets?> ResolveLocalBundleAsync(
        HostPlatformKind platform, ServerRuntimeIdentifier runtime, ServerInstallMode mode,
        string archivePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath) || !archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;
        var tools = await ResolveToolsAsync(platform, cancellationToken).ConfigureAwait(false);
        return tools is null ? null : new(tools, runtime, "server.zip", () => File.OpenRead(archivePath));
    }
}
