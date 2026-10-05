using System.Runtime.Versioning;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using System.IO.Compression;
using System.Security.Cryptography;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Constrained SCM management of the independent Mihomo component service.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsMihomoServiceManager
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _personalRoot;
    internal static void ConfigurePersonalRoot(string root) => _personalRoot = Path.GetFullPath(root);
    private static string Root => _personalRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RelaxKonOS", "Proxy");
    public static Task<PrivilegedOperationResult> InstallAsync() => SerializedAsync(InstallCoreAsync);
    private static async Task<PrivilegedOperationResult> InstallCoreAsync()
    {
        try
        {
            var versions = Path.Combine(Root, "engines", "mihomo", "versions");
            var release = File.ReadAllText(Path.Combine(versions, "current.txt")).Trim();
            var trusted = new MihomoRuntimeManifest().Find(MihomoRuntimeManifest.SupportedVersion) ?? throw new InvalidOperationException();
            if (release != trusted.ReleaseDirectoryId) return new(false, ProblemCode: PrivilegedProblemCode.InvalidRequest);
            var serviceBinary = await ImportTrustedBinaryAsync(trusted, versions);
            await WindowsComponentService.InstallAsync(ComponentKind.Mihomo, Root, serviceBinary,
                Path.Combine(Root, "config", "active.yaml"), Path.Combine(Root, "engines", "mihomo", "data"));
            return new(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return new(false, ProblemCode: PrivilegedProblemCode.InternalError); }
    }
    public static Task<PrivilegedOperationResult> RemoveAsync() => SerializedAsync(RemoveCoreAsync);
    private static async Task<PrivilegedOperationResult> RemoveCoreAsync()
    {
        await WindowsComponentService.RemoveAsync(ComponentKind.Mihomo, Root);
        var directory = Path.Combine(Root, "service-runtime");
        WindowsManagedRuntimePolicy.RequireNoLinks(directory);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        return new(true);
    }
    public static Task<PrivilegedOperationResult> ApplyAsync(ProxyMihomoServiceAction? action) => SerializedAsync(() => ApplyCoreAsync(action));
    private static async Task<PrivilegedOperationResult> ApplyCoreAsync(ProxyMihomoServiceAction? action)
    {
        if (action is null) return new(false, ProblemCode: PrivilegedProblemCode.InvalidRequest);
        switch (action.Value)
        {
            case ProxyMihomoServiceAction.Stop: await WindowsComponentService.StopAsync(ComponentKind.Mihomo, Root); break;
            case ProxyMihomoServiceAction.Disable:
                await WindowsComponentService.StopAsync(ComponentKind.Mihomo, Root);
                await WindowsComponentService.SetStartupAsync(ComponentKind.Mihomo, Root, false); break;
            case ProxyMihomoServiceAction.Enable: await WindowsComponentService.SetStartupAsync(ComponentKind.Mihomo, Root, true); break;
            case ProxyMihomoServiceAction.DaemonReload: break;
            case ProxyMihomoServiceAction.Restart:
            case ProxyMihomoServiceAction.TryRestart:
                await WindowsComponentService.StopAsync(ComponentKind.Mihomo, Root);
                goto case ProxyMihomoServiceAction.Start;
            case ProxyMihomoServiceAction.Start:
                var installed = await InstallCoreAsync();
                if (!installed.Success) return installed;
                await WindowsComponentService.StartAsync(ComponentKind.Mihomo, Root); break;
            default: return new(false, ProblemCode: PrivilegedProblemCode.InvalidRequest);
        }
        return new(true);
    }
    private static async Task<PrivilegedOperationResult> SerializedAsync(Func<Task<PrivilegedOperationResult>> action)
    {
        await Gate.WaitAsync();
        try { return await action(); }
        finally { Gate.Release(); }
    }
    private static async Task<string> ImportTrustedBinaryAsync(MihomoRuntimeRelease release, string versions)
    {
        var directory = Path.Combine(Root, "service-runtime", release.ReleaseDirectoryId);
        WindowsComponentService.ProtectDirectory(Path.Combine(Root, "service-runtime"));
        WindowsComponentService.ProtectDirectory(directory);
        var archive = Path.Combine(directory, "release.zip");
        if (!File.Exists(archive))
        {
            var source = Path.Combine(versions, release.ReleaseDirectoryId + ".zip");
            if (File.Exists(source))
            {
                WindowsManagedRuntimePolicy.RequireNoLinks(source);
                if (new FileInfo(source).Length > MihomoRuntimeManifest.MaximumArchiveBytes) throw new UnauthorizedAccessException();
                File.Copy(source, archive, true);
            }
            else
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                await using var input = await client.GetStreamAsync(release.DownloadUri);
                await using var output = File.Create(archive);
                var buffer = new byte[81920]; long total = 0;
                for (int read; (read = await input.ReadAsync(buffer)) != 0;)
                { if ((total += read) > MihomoRuntimeManifest.MaximumArchiveBytes) throw new IOException(); await output.WriteAsync(buffer.AsMemory(0, read)); }
            }
        }
        WindowsManagedRuntimePolicy.RequireNoLinks(archive);
        await using var package = File.OpenRead(archive);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(package)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            await package.DisposeAsync(); File.Delete(archive);
            throw new UnauthorizedAccessException();
        }
        package.Position = 0;
        using var zip = new ZipArchive(package, ZipArchiveMode.Read);
        var binaries = zip.Entries.Where(entry => entry.Name.StartsWith("mihomo", StringComparison.OrdinalIgnoreCase)
            && entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (binaries.Length != 1 || binaries[0].Length > MihomoRuntimeManifest.MaximumArchiveBytes) throw new UnauthorizedAccessException();
        var binary = Path.Combine(directory, "mihomo.exe");
        WindowsManagedRuntimePolicy.RequireNoLinks(binary);
        if (!File.Exists(binary))
        {
            await using var input = binaries[0].Open(); await using var output = File.Create(binary);
            await input.CopyToAsync(output);
        }
        else
        {
            await using var input = binaries[0].Open(); await using var existing = File.OpenRead(binary);
            var expectedHash = await SHA256.HashDataAsync(input);
            var actualHash = await SHA256.HashDataAsync(existing);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash)) throw new UnauthorizedAccessException();
        }
        return binary;
    }
}
