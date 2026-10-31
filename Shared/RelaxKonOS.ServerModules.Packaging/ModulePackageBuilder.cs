using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RelaxKonOS.ModulePackaging;

/// <summary>Inputs for an offline official module package build. The private key is read only for this call.</summary>
public sealed record ModulePackageBuildOptions(
    string PublishDirectory,
    string OutputDirectory,
    string ModuleId,
    string Version,
    string ReleaseId,
    string PublisherId,
    string Purpose,
    string Rid,
    string EntryAssembly,
    string EntryType,
    string BuildId,
    string SourceCommit,
    string SourceId,
    long Sequence,
    string ArtifactOrigin,
    string PrivateKeyPemPath,
    string KeyId,
    int ValiditySeconds,
    string HostApiMinimum = "1.0.0",
    string HostApiMaximumExclusive = "2.0.0",
    IReadOnlyList<string>? Capabilities = null,
    IReadOnlyList<ModulePackageDependency>? Dependencies = null,
    string Runtime = "in-process", IReadOnlyList<ModulePackagePrivilege>? Privileges = null, ModulePackageUi? Ui = null);

public sealed record ModulePackageBuildResult(ModulePackageManifest Manifest, string ManifestSha256,
    string ArchivePath, string ArchiveSha256, long ArchiveBytes, string CatalogPath, string PublicKeySpkiBase64);

/// <summary>
/// Builds a manifest, a `.rkmodule` ZIP and a signed catalog envelope from a published module
/// directory. It never installs or executes the module.
/// </summary>
public static class ModulePackageBuilder
{
    public static async Task<ModulePackageBuildResult> BuildAsync(ModulePackageBuildOptions options,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var publish = Path.GetFullPath(options.PublishDirectory);
        var output = Path.GetFullPath(options.OutputDirectory);
        if (!Directory.Exists(publish)) throw new ModulePackageException("module-package-build-path-invalid");
        Directory.CreateDirectory(output);
        if (!ModulePackageRules.Id(options.ModuleId) || !ModulePackageRules.Id(options.ReleaseId)
            || !ModulePackageRules.Id(options.PublisherId) || !ModulePackageRules.Id(options.SourceId)
            || !ModulePackageRules.Id(options.KeyId) || !ModulePackageRules.Id(options.BuildId)
            || options.Purpose is not ("official" or "development" or "third-party")
            || options.Rid is not ("linux-x64" or "linux-arm64" or "win-x64" or "win-arm64" or "osx-x64" or "osx-arm64")
            || options.ValiditySeconds is <= 0 or > 2678400 || options.Sequence <= 0
            || options.SourceCommit is not { Length: 40 or 64 } || !options.SourceCommit.All(char.IsAsciiHexDigitLower))
            throw new ModulePackageException("module-package-build-options-invalid");
        _ = ModulePackageRules.Version(options.Version);
        var origin = ParseOrigin(options.ArtifactOrigin);
        var entryRelative = options.EntryAssembly.Replace('\\', '/').TrimStart('/');
        if (entryRelative.Length == 0 || entryRelative.Split('/').Any(part => part is "." or ".." or ""))
            throw new ModulePackageException("module-package-build-entry-invalid");
        var entryPath = entryRelative.StartsWith("server/" + options.Rid + "/", StringComparison.Ordinal)
            ? entryRelative : "server/" + options.Rid + "/" + entryRelative;
        var files = new List<ModulePackageFile>();
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length is < 0 or > 268435456)
                throw new ModulePackageException("module-package-build-file-invalid");
            var relative = Path.GetRelativePath(publish, path).Replace('\\', '/');
            var packagePath = "server/" + options.Rid + "/" + relative;
            if (!ModulePackageRules.Path(packagePath)) throw new ModulePackageException("module-package-build-file-invalid");
            total += info.Length;
            if (total > 1073741824 || files.Count >= 4096) throw new ModulePackageException("module-package-build-limits");
            files.Add(new ModulePackageFile(packagePath, info.Length, await HashAsync(path, cancellationToken)));
        }
        if (files.Count == 0 || !files.Any(file => file.Path == entryPath))
            throw new ModulePackageException("module-package-build-entry-not-found");
        var manifest = new ModulePackageManifest(2, "server-module", options.ModuleId, options.ReleaseId,
            options.Version, options.PublisherId, options.Purpose, options.Rid,
            options.HostApiMinimum, options.HostApiMaximumExclusive, entryPath, options.EntryType,
            options.BuildId, options.SourceCommit, options.Dependencies?.ToArray() ?? [],
            options.Capabilities?.Distinct(StringComparer.Ordinal).ToArray() ?? [], files.ToArray(), options.Runtime, options.Privileges?.ToArray() ?? [],
            options.Ui ?? new(options.ModuleId, "", "", null, "UNLICENSED"));
        ModulePackageRules.Manifest(manifest);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ModulePackageJson.Options);
        var archivePath = Path.Combine(output, options.ModuleId + "-" + options.Version + "-" + options.Rid + ".rkmodule");
        if (!archivePath.StartsWith(output + Path.DirectorySeparatorChar, PathComparison))
            throw new ModulePackageException("module-package-build-path-invalid");
        await BuildArchiveAsync(archivePath, manifestBytes, publish, options.Rid, manifest.Files, cancellationToken);
        var archiveBytes = new FileInfo(archivePath).Length;
        var archiveSha = await HashAsync(archivePath, cancellationToken);
        var release = new ModulePackageRelease(manifest, Sha256(manifestBytes), archiveSha, archiveBytes,
            origin + Path.GetFileName(archivePath));
        var catalog = new ModulePackageCatalog(1, options.SourceId, options.Sequence, now, now.AddSeconds(options.ValiditySeconds),
            [release], [], []);
        var payload = JsonSerializer.SerializeToUtf8Bytes(catalog, ModulePackageJson.Options);
        using var key = ECDsa.Create();
        try { key.ImportFromPem(await File.ReadAllTextAsync(options.PrivateKeyPemPath, cancellationToken)); }
        catch (Exception error) when (error is ArgumentException or CryptographicException)
        { throw new ModulePackageException("module-package-build-key-invalid"); }
        if (key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            throw new ModulePackageException("module-package-build-key-invalid");
        var domain = Encoding.UTF8.GetBytes("RelaxKonOS.ServerModuleCatalog.v1\n");
        var signed = new byte[domain.Length + payload.Length];
        domain.CopyTo(signed, 0); payload.CopyTo(signed, domain.Length);
        var signature = key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var envelope = new SignedModulePackageCatalog(1, options.KeyId, Convert.ToBase64String(payload), Convert.ToBase64String(signature));
        var catalogPath = Path.Combine(output, "catalog.json");
        await File.WriteAllBytesAsync(catalogPath, JsonSerializer.SerializeToUtf8Bytes(envelope, ModulePackageJson.Options), cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(output, "manifest.json"), manifestBytes, cancellationToken);
        var spki = key.ExportSubjectPublicKeyInfo();
        await File.WriteAllTextAsync(Path.Combine(output, "public-key.spki"), Convert.ToBase64String(spki), cancellationToken);
        return new(manifest, Sha256(manifestBytes), archivePath, archiveSha, archiveBytes, catalogPath, Convert.ToBase64String(spki));
    }

    private static async Task BuildArchiveAsync(string path, byte[] manifest, string publish, string rid,
        IReadOnlyList<ModulePackageFile> files, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            65536, FileOptions.Asynchronous);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        using (var stream = zip.CreateEntry("manifest.json").Open()) await stream.WriteAsync(manifest, cancellationToken);
        var prefix = "server/" + rid + "/";
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = file.Path[prefix.Length..];
            var source = Path.Combine(publish, relative.Replace('/', Path.DirectorySeparatorChar));
            var entry = zip.CreateEntry(file.Path, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await using var target = entry.Open();
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(target, cancellationToken);
        }
    }

    private static string ParseOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/" || uri.AbsoluteUri != value)
            throw new ModulePackageException("module-package-build-origin-invalid");
        return value;
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
