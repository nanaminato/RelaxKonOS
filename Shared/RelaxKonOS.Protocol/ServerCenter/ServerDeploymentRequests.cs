using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>
/// 远端部署启动器的唯一入口请求。启动器只接受本记录与固定枚举，拒绝任意命令、脚本路径、
/// 服务名或删除路径。未知字段一律拒绝（<see cref="JsonUnmappedMemberHandling.Disallow"/>）。
/// </summary>
/// <param name="OperationId">同时作为幂等键：重复提交相同 <paramref name="OperationId"/> 返回既有结果，
/// 不同请求复用同一 ID 视为冲突。</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ServerDeploymentRequest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("kind")] ServerDeploymentKind Kind,
    [property: JsonPropertyName("options")] ServerDeploymentOptions? Options = null);

/// <summary>
/// 动作参数。所有字段都是固定枚举或受限字符串；本地离线包先经 SFTP 上传到受控暂存目录，
/// 请求里只携带受约束的文件名与摘要，绝不携带宿主路径。
/// </summary>
/// <param name="StagedPackageName">暂存目录内的裸文件名，由启动器按安全模式校验；不得包含路径分隔符或符号链接。</param>
/// <param name="PackageDigest">包 ZIP 的 SHA-256（十六进制）。签名与摘要都必须通过才能执行。</param>
/// <param name="ExpectedInstallationId">升级/修复/卸载/回滚必须与宿主实际安装标识一致，否则拒绝执行。</param>
/// <param name="Confirmed">破坏性动作（卸载删除数据、升级中断服务）的显式确认。</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ServerDeploymentOptions(
    [property: JsonPropertyName("source")] ServerPackageSourceKind Source,
    [property: JsonPropertyName("network")] ServerNetworkProfile Network,
    [property: JsonPropertyName("retention")] ServerDataRetention Retention = ServerDataRetention.Retain,
    [property: JsonPropertyName("mode")] ServerInstallMode? Mode = null,
    [property: JsonPropertyName("version")] string? Version = null,
    [property: JsonPropertyName("packageUri")] string? PackageUri = null,
    [property: JsonPropertyName("stagedPackageName")] string? StagedPackageName = null,
    [property: JsonPropertyName("packageDigest")] string? PackageDigest = null,
    [property: JsonPropertyName("expectedInstallationId")] string? ExpectedInstallationId = null,
    [property: JsonPropertyName("serverPort")] int? ServerPort = null,
    [property: JsonPropertyName("confirmed")] bool Confirmed = false);

/// <summary>
/// 安装标识：由部署引擎在首次安装时签发并写入安装清单，此后作为受管隧道的稳定身份。
/// 规范形式为 <c>rki-</c> 加 32 位小写十六进制（128 位随机值），与端口、地址无关。
/// </summary>
public static class ServerInstallationId
{
    public const string Prefix = "rki-";
    private const int HexLength = 32;

    /// <summary>生成新的安装标识。仅在部署引擎首次安装时调用。</summary>
    public static string NewId() => Prefix + Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();

    /// <summary>校验并返回规范形式的安装标识；非法输入返回 false。</summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var hex = trimmed[Prefix.Length..];
        if (hex.Length != HexLength) return false;
        foreach (var c in hex)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        normalized = Prefix + hex;
        return true;
    }

    public static bool IsValid(string? value) => TryNormalize(value, out _);
}

/// <summary>暂存包名与目标版本的受限校验，供启动器与客户端共用。</summary>
public static class ServerDeploymentInputRules
{
    private const int MaximumStagedNameLength = 128;

    /// <summary>暂存目录内的裸文件名：只允许字母、数字、点、下划线、连字符，必须以 .zip 结尾。</summary>
    public static bool IsSafeStagedPackageName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumStagedNameLength) return false;
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal)) return false;
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) return false;
        return true;
    }

    /// <summary>SHA-256 十六进制摘要（64 位十六进制，大小写不敏感）。</summary>
    public static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64) return false;
        foreach (var c in value)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        return true;
    }

    /// <summary>版本字符串：点分数字段加可选的预发布后缀，例如 <c>0.1.0</c> 或 <c>1.2.3-rc.1</c>。</summary>
    public static bool IsVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return false;
        foreach (var c in value)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+')) return false;
        return value.Any(char.IsAsciiDigit);
    }
}