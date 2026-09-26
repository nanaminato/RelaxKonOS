using System.Security.Cryptography;

namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>v1 发布签名算法。RSA-PSS + SHA-256 在 .NET 与 Android 均可用，因此作为必需算法。</summary>
public static class ServerReleaseSignatureAlgorithms
{
    public const string RsaPssSha256 = "rsa-pss-sha256";
}

/// <summary>
/// 发布清单、描述符与包布局的校验规则。客户端与启动器共用它，确保“包来源已确认”不是各端各写一套判断。
/// <para>签名语义：签名对象是制品的**原始文件字节**（<c>manifest.json</c> 或 <c>latest/{rid}.json</c>），
/// 签名单独放在同名 <c>*.sig</c> 文件中。<c>SignedSha256</c> 恒为这些原始字节的 SHA-256。
/// 这样 C#、Kotlin 与打包脚本不需要共享 JSON 规范化实现，避免跨语言字节不一致。</para>
/// </summary>
public static class ServerReleaseValidation
{
    /// <summary>校验包内清单结构。返回 null 表示通过，否则返回稳定的问题码。</summary>
    public static string? ValidateManifest(ServerReleaseManifestDto? manifest, ServerRuntimeIdentifier expectedRuntime)
    {
        if (manifest is null) return ServerDeploymentProblemCodes.PackageManifestInvalid;
        if (manifest.SchemaVersion != ServerDeploymentProtocol.Version) return ServerDeploymentProblemCodes.PackageManifestInvalid;
        if (!ServerDeploymentInputRules.IsVersion(manifest.Version)) return ServerDeploymentProblemCodes.PackageManifestInvalid;
        if (manifest.Runtime != expectedRuntime) return ServerDeploymentProblemCodes.PackageRuntimeMismatch;
        if (manifest.SupportedSystems is null || manifest.SupportedSystems.Count == 0)
            return ServerDeploymentProblemCodes.PackageManifestInvalid;
        if (manifest.Files is null || manifest.Files.Count == 0 || manifest.Payload is null)
            return ServerDeploymentProblemCodes.PackageManifestInvalid;

        var platform = expectedRuntime is ServerRuntimeIdentifier.WinX64 or ServerRuntimeIdentifier.WinArm64
            ? "windows" : "linux";
        if (!manifest.Payload.TryGetValue(platform, out var payload) || payload.Count == 0)
            return ServerDeploymentProblemCodes.PackageManifestInvalid;

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            var problem = ValidateFile(file);
            if (problem is not null) return problem;
            if (!paths.Add(file.Path)) return ServerDeploymentProblemCodes.PackageManifestInvalid;
        }
        foreach (var path in payload.Values)
            if (!IsSafeManifestPath(path) || !paths.Contains(path))
                return ServerDeploymentProblemCodes.PackageManifestInvalid;
        return null;
    }

    /// <summary>校验清单结构并验证其伴随签名。</summary>
    public static string? VerifyManifest(
        ReadOnlySpan<byte> manifestBytes,
        ServerReleaseManifestDto? manifest,
        ServerRuntimeIdentifier expectedRuntime,
        ServerReleaseSignatureDto? signature,
        string publicKeyPem,
        ServerReleaseTrustPolicy trustPolicy)
    {
        var structural = ValidateManifest(manifest, expectedRuntime);
        if (structural is not null) return structural;
        return ServerReleaseSignatureVerifier.Verify(manifestBytes, signature, publicKeyPem, trustPolicy);
    }

    public static string? ValidateFile(ServerReleaseFileDto? file)
    {
        if (file is null) return ServerDeploymentProblemCodes.PackageManifestInvalid;
        if (!IsSafeManifestPath(file.Path)) return ServerDeploymentProblemCodes.PackageLayoutUnsafe;
        if (!ServerDeploymentInputRules.IsSha256(file.Sha256)) return ServerDeploymentProblemCodes.PackageManifestInvalid;
        if (file.Length < 0) return ServerDeploymentProblemCodes.PackageManifestInvalid;
        return null;
    }

    /// <summary>
    /// 清单内路径必须是相对 POSIX 路径：无前导分隔符、无反斜杠、无 <c>..</c>、无空段。
    /// 解包时任何一条不满足都必须拒绝整个包，而不是跳过该条目。
    /// </summary>
    public static bool IsSafeManifestPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith('/') || path.StartsWith('\\')) return false;
        if (path.Contains('\\')) return false;
        if (path.Contains("..", StringComparison.Ordinal)) return false;
        if (path.Contains('\0')) return false;
        foreach (var segment in path.Split('/'))
            if (segment.Length == 0 || segment == ".") return false;
        return true;
    }

    /// <summary>校验下载描述符结构。签名是伴随文件，因此这里只做结构校验。</summary>
    public static string? ValidateDescriptor(ServerReleaseDescriptorDto? descriptor)
    {
        if (descriptor is null) return ServerDeploymentProblemCodes.PackageUnavailable;
        if (descriptor.SchemaVersion != ServerDeploymentProtocol.Version) return ServerDeploymentProblemCodes.PackageManifestInvalid;
        if (!ServerDeploymentInputRules.IsVersion(descriptor.Version)) return ServerDeploymentProblemCodes.PackageManifestInvalid;
        if (!ServerDeploymentInputRules.IsSha256(descriptor.Sha256)) return ServerDeploymentProblemCodes.PackageDigestMismatch;
        return null;
    }

    /// <summary>校验描述符结构并验证其伴随签名。缺少签名直接拒绝。</summary>
    public static string? VerifyDescriptor(
        ReadOnlySpan<byte> descriptorBytes,
        ServerReleaseDescriptorDto? descriptor,
        ServerReleaseSignatureDto? signature,
        string publicKeyPem,
        ServerReleaseTrustPolicy trustPolicy)
    {
        var structural = ValidateDescriptor(descriptor);
        if (structural is not null) return structural;
        return ServerReleaseSignatureVerifier.Verify(descriptorBytes, signature, publicKeyPem, trustPolicy);
    }
}

/// <summary>
/// 发布签名校验。签名覆盖制品原始字节的 SHA-256，客户端用内置可信公钥验证，并确认 <c>keyId</c> 在信任策略内。
/// </summary>
public static class ServerReleaseSignatureVerifier
{
    /// <summary>
    /// 验证一段制品字节的签名。
    /// </summary>
    /// <param name="signedBytes">制品原始文件字节（<c>manifest.json</c> 或目录项 JSON）。</param>
    /// <param name="signature">签名记录，携带算法、keyId 与 Base64 签名。</param>
    /// <param name="publicKeyPem">该 keyId 对应的 SubjectPublicKeyInfo PEM。</param>
    /// <param name="trustPolicy">发布信任策略；未受信任的 keyId 直接拒绝。</param>
    public static string? Verify(
        ReadOnlySpan<byte> signedBytes,
        ServerReleaseSignatureDto? signature,
        string publicKeyPem,
        ServerReleaseTrustPolicy trustPolicy)
    {
        if (signature is null) return ServerDeploymentProblemCodes.PackageSignatureInvalid;
        if (signature.SchemaVersion != ServerDeploymentProtocol.Version) return ServerDeploymentProblemCodes.PackageSignatureInvalid;
        if (signature.Algorithm != ServerReleaseSignatureAlgorithms.RsaPssSha256)
            return ServerDeploymentProblemCodes.PackageSignatureInvalid;
        if (string.IsNullOrWhiteSpace(signature.KeyId) || string.IsNullOrWhiteSpace(signature.Signature))
            return ServerDeploymentProblemCodes.PackageSignatureInvalid;
        if (string.IsNullOrWhiteSpace(publicKeyPem)) return ServerDeploymentProblemCodes.PackageTrustRootMissing;
        if (!trustPolicy.IsTrustedKey(signature.KeyId) && !trustPolicy.AcceptsUntrustedSource)
            return ServerDeploymentProblemCodes.PackageSignatureInvalid;

        var digest = Convert.ToHexString(SHA256.HashData(signedBytes)).ToLowerInvariant();
        if (!string.Equals(digest, signature.SignedSha256, StringComparison.OrdinalIgnoreCase))
            return ServerDeploymentProblemCodes.PackageSignatureInvalid;

        byte[] signatureBytes;
        try { signatureBytes = Convert.FromBase64String(signature.Signature); }
        catch (FormatException) { return ServerDeploymentProblemCodes.PackageSignatureInvalid; }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            var ok = rsa.VerifyData(signedBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            return ok ? null : ServerDeploymentProblemCodes.PackageSignatureInvalid;
        }
        catch (CryptographicException)
        {
            return ServerDeploymentProblemCodes.PackageSignatureInvalid;
        }
        catch (ArgumentException)
        {
            return ServerDeploymentProblemCodes.PackageSignatureInvalid;
        }
    }
}
