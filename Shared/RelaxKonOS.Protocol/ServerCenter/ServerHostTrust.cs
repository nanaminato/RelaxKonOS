using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>
/// 一条已确认的 SSH 主机密钥记录。按 <c>(host, port, algorithm)</c> 保存：同一台宿主可以同时提供
/// rsa 与 ed25519 两种主机密钥，它们是两条独立记录，改动其一不影响另一条。
/// <para>主机密钥与指纹都不是秘密，可以明文保存；但必须抗意外覆盖，并在变化时阻断写操作。</para>
/// </summary>
/// <param name="Host">连接时使用的主机名或 IP，按规范化形式保存（小写、去括号）。</param>
/// <param name="Port">SSH 端口。默认 22 也显式记录，避免默认端口与显式端口被当成两台宿主。</param>
/// <param name="Algorithm">SSH 主机密钥算法名，例如 <c>ssh-ed25519</c> 或 <c>rsa-sha2-256</c>。</param>
/// <param name="PublicKeyBase64">主机公钥的原始字节（SSH wire 格式）的 Base64，用于与观测值逐字节比较。</param>
/// <param name="Fingerprint">展示与诊断用的 <c>SHA256:</c> 指纹；不参与匹配判断。</param>
/// <param name="ConfirmedAtUtc">用户确认该密钥的时间。</param>
public sealed record ServerHostKeyRecord(
    string Host,
    int Port,
    string Algorithm,
    string PublicKeyBase64,
    string Fingerprint,
    DateTimeOffset ConfirmedAtUtc);

/// <summary>
/// SSH 主机密钥的固定规则。桌面与 Android 必须给出同一判断，否则同一台宿主会在两端得到不同的信任结论。
/// 任何实现都不得提供绕过入口（例如等价于 <c>StrictHostKeyChecking=no</c> 的静默接受）。
/// </summary>
public static class ServerHostTrustRules
{
    /// <summary>SSH 的默认端口。显式写出端口时也用它，保证 <c>host</c> 与 <c>host:22</c> 是同一端点。</summary>
    public const int DefaultPort = 22;

    /// <summary>指纹前缀，与 OpenSSH 展示形式一致。</summary>
    public const string FingerprintPrefix = "SHA256:";

    /// <summary>端点键：<c>host:port</c>。主机名统一小写，端口总是显式给出。</summary>
    public static string EndpointKey(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return string.Create(CultureInfo.InvariantCulture, $"{NormalizeHost(host)}:{port}");
    }

    /// <summary>规范化主机名：去空白、去 IPv6 方括号、转小写。DNS 名称不区分大小写。</summary>
    public static string NormalizeHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var trimmed = host.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']') trimmed = trimmed[1..^1];
        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// 由主机公钥原始字节计算 <c>SHA256:</c> 指纹（Base64，去掉 <c>=</c> 填充），与 OpenSSH 输出一致。
    /// </summary>
    public static string Fingerprint(ReadOnlySpan<byte> publicKeyBlob)
    {
        var digest = SHA256.HashData(publicKeyBlob);
        return FingerprintPrefix + Convert.ToBase64String(digest).TrimEnd('=');
    }

    /// <summary>指纹的展示形式：按 4 个字符分组，便于用户逐段核对。</summary>
    public static string GroupedFingerprint(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        var body = fingerprint.StartsWith(FingerprintPrefix, StringComparison.Ordinal)
            ? fingerprint[FingerprintPrefix.Length..]
            : fingerprint;
        var builder = new StringBuilder(fingerprint.StartsWith(FingerprintPrefix, StringComparison.Ordinal)
            ? FingerprintPrefix.Length + body.Length + (body.Length / 4)
            : body.Length + (body.Length / 4));
        if (fingerprint.StartsWith(FingerprintPrefix, StringComparison.Ordinal)) builder.Append(FingerprintPrefix);
        for (var i = 0; i < body.Length; i += 4)
        {
            if (i > 0) builder.Append(' ');
            builder.Append(body, i, Math.Min(4, body.Length - i));
        }
        return builder.ToString();
    }

    /// <summary>校验指纹形状：<c>SHA256:</c> 加 43 个 Base64 字符（256 位去掉填充后的长度）。</summary>
    public static bool IsFingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(FingerprintPrefix, StringComparison.Ordinal)) return false;
        var body = value[FingerprintPrefix.Length..];
        if (body.Length != 43) return false;
        foreach (var c in body)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/')) return false;
        return true;
    }

    /// <summary>同端点同算法的既有记录。</summary>
    public static ServerHostKeyRecord? Find(
        IEnumerable<ServerHostKeyRecord> known, string host, int port, string algorithm)
    {
        var key = EndpointKey(host, port);
        foreach (var record in known)
        {
            if (string.Equals(EndpointKey(record.Host, record.Port), key, StringComparison.Ordinal)
                && string.Equals(record.Algorithm, algorithm, StringComparison.Ordinal))
                return record;
        }
        return null;
    }

    /// <summary>
    /// 判定本次观测到的宿主密钥。首次见到某端点或某算法返回 <see cref="ServerHostKeyTrust.Unknown"/>，
    /// 必须由用户核对指纹后才可继续；与既有记录不一致返回 <see cref="ServerHostKeyTrust.Changed"/>，
    /// 该状态下所有写操作都必须被阻断。
    /// </summary>
    public static ServerHostKeyTrust Evaluate(
        IEnumerable<ServerHostKeyRecord> known, string host, int port, string algorithm, ReadOnlySpan<byte> publicKeyBlob)
    {
        var existing = Find(known, host, port, algorithm);
        if (existing is null) return ServerHostKeyTrust.Unknown;
        var observed = Convert.ToBase64String(publicKeyBlob);
        return string.Equals(existing.PublicKeyBase64, observed, StringComparison.Ordinal)
            ? ServerHostKeyTrust.Trusted
            : ServerHostKeyTrust.Changed;
    }

    /// <summary>
    /// 写操作门禁。只有 <see cref="ServerHostKeyTrust.Trusted"/> 允许安装、升级、修复、卸载、回滚；
    /// <see cref="ServerHostKeyTrust.Unknown"/> 在用户当次确认后即转为可信，因此它是「需要确认」而不是「禁止」。
    /// </summary>
    public static bool BlocksWriteOperations(ServerHostKeyTrust trust) => trust == ServerHostKeyTrust.Changed;

    /// <summary>该信任状态对应的稳定问题码；<see cref="ServerHostKeyTrust.Trusted"/> 时为 null。</summary>
    public static string? ProblemCode(ServerHostKeyTrust trust) => trust switch
    {
        ServerHostKeyTrust.Unknown => ServerDeploymentProblemCodes.HostKeyUnknown,
        ServerHostKeyTrust.Changed => ServerDeploymentProblemCodes.HostKeyChanged,
        _ => null
    };

    /// <summary>观测到密钥变化时，替换既有记录的规则：同端点同算法只保留一条，且必须由用户显式确认。</summary>
    public static IReadOnlyList<ServerHostKeyRecord> Replace(
        IEnumerable<ServerHostKeyRecord> known, ServerHostKeyRecord confirmed)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        var key = EndpointKey(confirmed.Host, confirmed.Port);
        var result = known
            .Where(r => !(string.Equals(EndpointKey(r.Host, r.Port), key, StringComparison.Ordinal)
                          && string.Equals(r.Algorithm, confirmed.Algorithm, StringComparison.Ordinal)))
            .ToList();
        result.Add(confirmed);
        return result;
    }
}