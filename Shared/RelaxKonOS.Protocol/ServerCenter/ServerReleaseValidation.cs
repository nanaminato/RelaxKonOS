namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>
/// 发布清单、描述符与包布局的校验规则。
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

    /// <summary>校验下载描述符结构。</summary>
    public static string? ValidateDescriptor(ServerReleaseDescriptorDto? descriptor)
    {
        if (descriptor is null) return ServerDeploymentProblemCodes.PackageUnavailable;
        if (descriptor.SchemaVersion != ServerDeploymentProtocol.Version) return ServerDeploymentProblemCodes.PackageManifestInvalid;
        if (!ServerDeploymentInputRules.IsVersion(descriptor.Version)) return ServerDeploymentProblemCodes.PackageManifestInvalid;
        if (!ServerDeploymentInputRules.IsSha256(descriptor.Sha256)) return ServerDeploymentProblemCodes.PackageDigestMismatch;
        return null;
    }
}
