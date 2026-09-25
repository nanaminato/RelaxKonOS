using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>客户端在 SFTP 上传前对整个离线发布 ZIP 进行验证的结果。</summary>
public sealed record ServerReleaseArchiveVerification(
    string? ProblemCode, ServerReleaseManifestDto? Manifest, string? ArchiveSha256)
{
    public bool Verified => ProblemCode is null && Manifest is not null;
}

/// <summary>
/// 发布签名绑定原始 manifest.json，清单绑定包内每个文件；ZIP 摘要只负责传输完整性。
/// 信任根由客户端内置或显式开发配置提供，包中携带的公钥永远不被自动信任。
/// </summary>
public static class ServerReleaseArchiveVerifier
{
    private const int MaximumManifestBytes = 1024 * 1024;
    private const int MaximumSignatureBytes = 16 * 1024;
    private const int MaximumEntries = 20000;
    private const long MaximumPayloadBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>Verify the archive, then materialize only its signed files into a new directory.</summary>
    public static ServerReleaseArchiveVerification VerifyAndExtract(
        Stream archiveStream,
        string destination,
        ServerReleasePackageKind expectedKind,
        ServerRuntimeIdentifier expectedRuntime,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        string? expectedArchiveSha256 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var result = Verify(archiveStream, expectedKind, expectedRuntime, trustedPublicKeys, expectedArchiveSha256);
        if (!result.Verified) return result;

        var target = Path.GetFullPath(destination);
        if (Directory.Exists(target) || File.Exists(target))
            throw new IOException("The extraction destination already exists.");
        var parent = Path.GetDirectoryName(target) ?? throw new IOException("The extraction destination has no parent.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, ".release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            archiveStream.Position = 0;
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > MaximumEntries)
                throw new InvalidDataException("The release contains too many entries.");
            var signedFiles = result.Manifest!.Files.ToDictionary(item => item.Path, StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                var relative = entry.FullName;
                if (!ServerReleaseValidation.IsSafeManifestPath(relative) ||
                    ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                    relative is not ("manifest.json" or "manifest.json.sig") && !signedFiles.ContainsKey(relative))
                    throw new InvalidDataException("The archive changed during extraction.");
                var output = Path.Combine(temporary, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var input = entry.Open();
                using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long length = 0;
                int count;
                while ((count = input.Read(buffer)) > 0)
                {
                    length = checked(length + count);
                    if (length > entry.Length) throw new InvalidDataException("The archive changed during extraction.");
                    file.Write(buffer, 0, count);
                    digest.AppendData(buffer, 0, count);
                }
                if (length != entry.Length) throw new InvalidDataException("The archive changed during extraction.");
                if (signedFiles.TryGetValue(relative, out var signed) &&
                    (length != signed.Length ||
                     !Convert.ToHexString(digest.GetHashAndReset()).Equals(signed.Sha256, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("A signed file changed during extraction.");
                if (OperatingSystem.IsLinux())
                {
                    var permissions = (UnixFileMode)((entry.ExternalAttributes >> 16) & 0x1FF);
                    File.SetUnixFileMode(output, permissions == 0
                        ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                        : permissions);
                }
            }
            archiveStream.Position = 0;
            if (!HashStream(archiveStream).Equals(result.ArchiveSha256, StringComparison.Ordinal))
                throw new InvalidDataException("The archive changed during extraction.");
            Directory.Move(temporary, target);
            return result;
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    public static ServerReleaseArchiveVerification Verify(
        Stream archiveStream,
        ServerReleasePackageKind expectedKind,
        ServerRuntimeIdentifier expectedRuntime,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        string? expectedArchiveSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        ArgumentNullException.ThrowIfNull(trustedPublicKeys);
        if (!archiveStream.CanRead || !archiveStream.CanSeek)
            throw new ArgumentException("A readable, seekable archive stream is required.", nameof(archiveStream));

        var originalPosition = archiveStream.Position;
        try
        {
            archiveStream.Position = 0;
            var archiveDigest = HashStream(archiveStream);
            if (expectedArchiveSha256 is not null &&
                (!ServerDeploymentInputRules.IsSha256(expectedArchiveSha256) ||
                 !string.Equals(archiveDigest, expectedArchiveSha256, StringComparison.OrdinalIgnoreCase)))
                return new(ServerDeploymentProblemCodes.PackageDigestMismatch, null, archiveDigest);

            archiveStream.Position = 0;
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > MaximumEntries)
                return new(ServerDeploymentProblemCodes.PackageLayoutUnsafe, null, archiveDigest);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                {
                    if (!ServerReleaseValidation.IsSafeManifestPath(entry.FullName.TrimEnd('/')))
                        return new(ServerDeploymentProblemCodes.PackageLayoutUnsafe, null, archiveDigest);
                    continue;
                }
                if (!ServerReleaseValidation.IsSafeManifestPath(entry.FullName) ||
                    !entries.TryAdd(entry.FullName, entry) ||
                    ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    return new(ServerDeploymentProblemCodes.PackageLayoutUnsafe, null, archiveDigest);
            }

            if (!entries.TryGetValue("manifest.json", out var manifestEntry) ||
                !entries.TryGetValue("manifest.json.sig", out var signatureEntry) ||
                manifestEntry.Length > MaximumManifestBytes || signatureEntry.Length > MaximumSignatureBytes)
                return new(ServerDeploymentProblemCodes.PackageSignatureInvalid, null, archiveDigest);

            var manifestBytes = ReadBounded(manifestEntry, MaximumManifestBytes);
            var signatureBytes = ReadBounded(signatureEntry, MaximumSignatureBytes);
            var manifest = JsonSerializer.Deserialize<ServerReleaseManifestDto>(manifestBytes, RelaxKonOSJsonOptions.Default);
            var signature = JsonSerializer.Deserialize<ServerReleaseSignatureDto>(signatureBytes, RelaxKonOSJsonOptions.Default);
            if (manifest is null || signature is null)
                return new(ServerDeploymentProblemCodes.PackageManifestInvalid, null, archiveDigest);
            if (manifest.PackageKind != expectedKind)
                return new(ServerDeploymentProblemCodes.PackageManifestInvalid, null, archiveDigest);
            if (string.IsNullOrWhiteSpace(signature.KeyId) ||
                !trustedPublicKeys.TryGetValue(signature.KeyId, out var publicKey))
                return new(ServerDeploymentProblemCodes.PackageTrustRootMissing, null, archiveDigest);

            var trust = new ServerReleaseTrustPolicy([signature.KeyId]);
            var problem = ServerReleaseValidation.VerifyManifest(
                manifestBytes, manifest, expectedRuntime, signature, publicKey, trust);
            if (problem is not null) return new(problem, null, archiveDigest);

            var signedFiles = manifest.Files.ToDictionary(item => item.Path, StringComparer.Ordinal);
            long totalLength = 0;
            foreach (var file in manifest.Files)
            {
                if (!entries.TryGetValue(file.Path, out var entry) || entry.Length != file.Length ||
                    file.Length > MaximumPayloadBytes - totalLength)
                    return new(ServerDeploymentProblemCodes.PackageManifestInvalid, null, archiveDigest);
                totalLength += file.Length;
                using var content = entry.Open();
                var digest = HashEntry(content, file.Length);
                if (!string.Equals(digest, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new(ServerDeploymentProblemCodes.PackageDigestMismatch, null, archiveDigest);
            }

            foreach (var path in entries.Keys)
                if (path is not ("manifest.json" or "manifest.json.sig") && !signedFiles.ContainsKey(path))
                    return new(ServerDeploymentProblemCodes.PackageLayoutUnsafe, null, archiveDigest);

            return new(null, manifest, archiveDigest);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or OverflowException)
        {
            return new(ServerDeploymentProblemCodes.PackageManifestInvalid, null, null);
        }
        finally
        {
            archiveStream.Position = originalPosition;
        }
    }

    private static byte[] ReadBounded(ZipArchiveEntry entry, int maximum)
    {
        if (entry.Length > maximum) throw new InvalidDataException("Release metadata exceeds its size limit.");
        using var content = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = content.Read(chunk)) > 0)
        {
            if (count > maximum - buffer.Length)
                throw new InvalidDataException("Release metadata exceeds its size limit.");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    private static string HashStream(Stream stream)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string? HashEntry(Stream stream, long expectedLength)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long received = 0;
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            if (count > expectedLength - received) return null;
            received += count;
            hash.AppendData(buffer, 0, count);
        }
        return received == expectedLength ? Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() : null;
    }
}
