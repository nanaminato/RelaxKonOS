using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>
/// 最近一次经 SSH 核验的部署状态。它是**带核验时间的缓存**，不是实时健康：
/// 界面必须展示 <see cref="VerifiedAtUtc"/>，且离线缓存不得冒充当前状态。
/// </summary>
/// <param name="Installed">宿主上是否存在 RelaxKonOS 受管安装。</param>
/// <param name="InstallationId">本次核验读到的安装标识；仅当 <paramref name="Installed"/> 为 true 时才可能非空。</param>
/// <param name="Healthy">核验时经 loopback 健康端点得到的实际结果。</param>
/// <param name="VerifiedAtUtc">该状态的核验时间，界面“通过 SSH 于 … 验证”必须使用它。</param>
public sealed record ServerHostVerifiedState(
    bool Installed,
    ServerInstallMode? Mode,
    string? InstallationId,
    string? Version,
    string? ListenUrl,
    bool Healthy,
    DateTimeOffset VerifiedAtUtc);

/// <summary>
/// 宿主目标：用户显式添加的一台受管宿主。它是**宿主生命周期资料**，
/// 既不是 RelaxKonOS 登录记录，也不是 SSH 凭据。
/// <para>一个宿主目标可以关联多个登录，也可以在首次安装前没有任何登录。删除宿主目标只影响本机管理资料；
/// 卸载服务端是需要远端确认的独立动作。</para>
/// <para>SSH 凭据、主机指纹与操作回执只保存在本设备，不同步到 Workspace。</para>
/// </summary>
/// <param name="HostId">本机稳定标识，由规范化端点派生；同一 <c>(host, port)</c> 只有一个目标。</param>
/// <param name="DisplayName">用户起的宿主名。登录表单展示它，而不是临时 loopback 端口。</param>
/// <param name="SshHost">SSH 主机名或 IP，按规范化形式保存（小写、去 IPv6 方括号）。</param>
/// <param name="SshPort">SSH 端口，总是显式记录。</param>
/// <param name="SshUserName">用于管理该宿主的 SSH 用户；SSH 凭据按它与端点绑定。</param>
/// <param name="InstallationId">受管安装标识：安装成功后写入，作为受管隧道的稳定 <c>serviceId</c>；安装前为 null。</param>
/// <param name="LastVerified">最近一次经 SSH 核验的部署状态；从未核验时为 null。</param>
public sealed record ServerHostTarget(
    string HostId,
    string DisplayName,
    string SshHost,
    int SshPort,
    string SshUserName,
    string? InstallationId,
    ServerHostVerifiedState? LastVerified,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastUsedAtUtc);

/// <summary>
/// 宿主目标的共用规则。桌面与 Android 必须给出同一标识与同一判断，
/// 否则同一台宿主会在两端被当成两条记录。
/// </summary>
public static class ServerHostTargetRules
{
    /// <summary>本机宿主标识前缀，便于与安装标识、登录标识区分。</summary>
    public const string HostIdPrefix = "rkhost-";

    /// <summary>用户起名的长度上限；超出即视为无效输入。</summary>
    public const int MaximumDisplayNameLength = 64;

    /// <summary>宿主目标的去重键：<c>host:port</c>。不含 SSH 用户，因为安装与主机密钥都属于宿主。</summary>
    public static string EndpointIdentity(string host, int port) => ServerHostTrustRules.EndpointKey(host, port);

    /// <summary>
    /// 由规范化端点派生的稳定本机标识。它不含秘密，也不随端口以外的任何输入变化，
    /// 因此重复添加同一宿主不会产生第二条记录。
    /// </summary>
    public static string HostId(string host, int port)
    {
        var key = EndpointIdentity(host, port);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return HostIdPrefix + Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>是否为本规则生成的宿主标识。</summary>
    public static bool IsHostId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(HostIdPrefix, StringComparison.Ordinal)) return false;
        var hex = value[HostIdPrefix.Length..];
        if (hex.Length != 16) return false;
        foreach (var c in hex)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }

    /// <summary>规范化展示名：去空白并限长；为空时回落到端点键，绝不编造主机名。</summary>
    public static string NormalizeDisplayName(string? displayName, string host, int port)
    {
        var trimmed = displayName?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return EndpointIdentity(host, port);
        return trimmed.Length <= MaximumDisplayNameLength ? trimmed : trimmed[..MaximumDisplayNameLength];
    }

    /// <summary>
    /// 校验 SSH 端点输入。端口必须是显式有效的 1–65535，用户名不得为空：
    /// 没有用户名的「宿主目标」无法建立受管连接，必须在添加时就拒绝。
    /// </summary>
    public static bool IsValidEndpoint(string? host, int port, string? userName) =>
        !string.IsNullOrWhiteSpace(host)
        && port is > 0 and <= 65535
        && !string.IsNullOrWhiteSpace(userName);

    /// <summary>
    /// 新建宿主目标。SSH 用户与展示名在此规范化，安装标识与核验状态留空——
    /// 它们只能由安装或探测结果写入，不能由用户表单直接声称。
    /// </summary>
    public static ServerHostTarget Create(
        string host, int port, string userName, string? displayName, DateTimeOffset nowUtc)
    {
        if (!IsValidEndpoint(host, port, userName))
            throw new ArgumentException("An SSH host, port and user name are required.", nameof(host));

        var normalizedHost = ServerHostTrustRules.NormalizeHost(host);
        return new ServerHostTarget(
            HostId: HostId(normalizedHost, port),
            DisplayName: NormalizeDisplayName(displayName, normalizedHost, port),
            SshHost: normalizedHost,
            SshPort: port,
            SshUserName: userName.Trim(),
            InstallationId: null,
            LastVerified: null,
            CreatedAtUtc: nowUtc,
            LastUsedAtUtc: nowUtc);
    }

    /// <summary>
    /// 用一次核验结果更新宿主目标。安装标识只在核验确实给出一个**合法**标识时改写：
    /// 探测失败或宿主上尚无安装（null）不得清掉已知的受管标识，否则会丢失隧道身份。
    /// </summary>
    public static ServerHostTarget ApplyVerifiedState(
        ServerHostTarget target, ServerHostVerifiedState verified, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(verified);

        var installationId = target.InstallationId;
        if (verified.Installed && ServerInstallationId.TryNormalize(verified.InstallationId, out var canonical))
            installationId = canonical;

        return target with
        {
            InstallationId = installationId,
            LastVerified = verified,
            LastUsedAtUtc = nowUtc
        };
    }

    /// <summary>
    /// 由只读宿主快照得出核验状态。<see cref="ServerHostSnapshotDto"/> 是 SSH 侧探测的权威形状，
    /// 这里只做投影，不重新判断宿主状态。
    /// </summary>
    public static ServerHostVerifiedState VerifiedStateFrom(ServerHostSnapshotDto snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new ServerHostVerifiedState(
            snapshot.Installed,
            snapshot.Mode,
            snapshot.InstallationId,
            snapshot.Version,
            snapshot.ListenUrl,
            snapshot.Healthy,
            snapshot.VerifiedAtUtc);
    }

    /// <summary>受管隧道所需的安装标识是否已就绪。</summary>
    public static bool HasManagedInstallation(ServerHostTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return ServerInstallationId.IsValid(target.InstallationId);
    }

    /// <summary>
    /// 该宿主是否可与某个登录关联。只有受管安装标识一致才成立：
    /// 相同 IP、URL 文本或 DNS 解析都不足以合并，否则会把两台不同安装当成同一台。
    /// </summary>
    public static bool MatchesInstallation(ServerHostTarget target, string? serviceId)
    {
        ArgumentNullException.ThrowIfNull(target);
        return ServerInstallationId.TryNormalize(target.InstallationId, out var managed)
               && string.Equals(managed, serviceId, StringComparison.Ordinal);
    }

    /// <summary>宿主详情页的状态标签，供两端复用同一套判断。</summary>
    public static ServerHostTargetStatus Status(ServerHostTarget target, ServerHostKeyTrust trust)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (trust == ServerHostKeyTrust.Changed) return ServerHostTargetStatus.HostKeyChanged;
        if (trust == ServerHostKeyTrust.Unknown) return ServerHostTargetStatus.HostKeyUnknown;
        var verified = target.LastVerified;
        if (verified is null) return ServerHostTargetStatus.Unverified;
        if (!verified.Installed) return ServerHostTargetStatus.ReachableNotInstalled;
        return verified.Healthy ? ServerHostTargetStatus.InstalledHealthy : ServerHostTargetStatus.ServiceUnhealthy;
    }

    /// <summary>展示用的“最近核验”文案片段；从未核验时为 null，界面不得伪造时间。</summary>
    public static string? VerifiedLabel(ServerHostTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var verified = target.LastVerified;
        return verified is null
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"SSH {verified.VerifiedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
    }
}

/// <summary>
/// 宿主目标在详情页的状态。API 可达、SSH 可达、服务已安装是三个独立事实，
/// 因此这里只描述宿主侧结论，不把「API 不可达」推断成「需要重新安装」。
/// </summary>
public enum ServerHostTargetStatus
{
    /// <summary>已添加但尚未完成任何 SSH 核验。</summary>
    Unverified,

    /// <summary>首次见到该端点或该算法的主机密钥，必须由用户核对指纹。</summary>
    HostKeyUnknown,

    /// <summary>主机密钥与固定记录不一致，所有写操作被阻断。</summary>
    HostKeyChanged,

    /// <summary>SSH 可达，但宿主上尚无 RelaxKonOS 受管安装。</summary>
    ReachableNotInstalled,

    /// <summary>已安装且经 loopback 健康检查通过。</summary>
    InstalledHealthy,

    /// <summary>已安装但服务异常，可修复或恢复上次版本。</summary>
    ServiceUnhealthy
}