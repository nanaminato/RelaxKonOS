using System.Globalization;
using System.Net;

namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>本次会话的传输方式。它只描述「怎么到达」，不参与登录身份。</summary>
public enum ServerConnectionTransportKind { Direct, SshTunnel }

/// <summary>
/// 连接解析器对外的当前结论：稳定身份 <see cref="Identity"/> 加上本次会话实际使用的地址。
/// <para>登录记录、保险箱与 API 会话都以 <see cref="ServerConnectionIdentity.ServiceId"/> 为键；
/// <see cref="ServerConnectionIdentity.EffectiveBaseUrl"/> 只用于本会话发请求。</para>
/// </summary>
/// <param name="LocalPort">受管隧道绑定的 loopback 端口；直连时为 null。</param>
/// <param name="ResolvedAtUtc">该解析结果的生成时间。隧道重建或换端口后必须刷新。</param>
public sealed record ServerConnectionResolution(
    ServerConnectionIdentity Identity,
    ServerConnectionTransportKind Transport,
    int? LocalPort,
    DateTimeOffset ResolvedAtUtc);

/// <summary>
/// loopback 隧道与连接解析的共用规则。客户端内置的 SSH 转发只能绑定 loopback：把服务暴露到
/// 非 loopback 地址会把「已登录用户才有权访问的服务」变成局域网可达，必须由用户显式选择
/// 受信任 TLS 入口，而不是隧道默认行为。
/// </summary>
public static class ServerTunnelRules
{
    /// <summary>隧道唯一允许绑定的地址。不要用 <c>localhost</c>，它可能解析到非回环地址。</summary>
    public const string LoopbackHost = "127.0.0.1";

    /// <summary>IPv6 回环地址，仅用于识别，不作为默认绑定目标。</summary>
    public const string LoopbackHostV6 = "::1";

    /// <summary>端口 0 表示由操作系统分配空闲端口；这正是隧道应使用的方式，避免固定端口冲突。</summary>
    public const int EphemeralPort = 0;

    /// <summary>是否回环地址（IPv4 <c>127.0.0.0/8</c> 或 IPv6 <c>::1</c>）。</summary>
    public static bool IsLoopback(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    /// <summary>字符串形式是否为可绑定的回环地址。</summary>
    public static bool IsLoopbackHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && IPAddress.TryParse(host.Trim(), out var address) && IsLoopback(address);

    /// <summary>
    /// 隧道监听地址是否可接受。任何非回环地址都必须被拒绝，这是硬约束，不提供配置开关。
    /// </summary>
    public static bool IsAcceptableTunnelBindAddress(string? host) => IsLoopbackHost(host);

    /// <summary>
    /// 由隧道本地端口与基础路径构造本次会话的 HTTP 地址。隧道终止的是明文回环连接，
    /// 因此只使用 <c>http</c>；TLS 由远端监听配置决定，不由本地隧道伪造。
    /// </summary>
    public static string BuildLoopbackBaseUrl(int port, string? basePath = null)
    {
        if (port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var path = string.IsNullOrWhiteSpace(basePath) ? string.Empty : "/" + basePath.Trim().Trim('/');
        return string.Create(CultureInfo.InvariantCulture, $"http://{LoopbackHost}:{port}{path}");
    }

    /// <summary>解析回环地址的端口；不是回环地址或没有显式端口时返回 null。</summary>
    public static int? TryGetLoopbackPort(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)) return null;
        if (!IsLoopbackHost(uri.Host)) return null;

        // Uri.Port substitutes the scheme default (80/443) when the URL carries no port, which would
        // silently turn a portless loopback URL into port 80. A tunnel address always states its
        // ephemeral port explicitly, so read the authority text instead of trusting Uri.Port.
        var authority = uri.Authority;
        var separator = authority.LastIndexOf(':');
        if (separator < 0 || separator == authority.Length - 1) return null;
        if (!int.TryParse(authority[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            return null;
        return port is > 0 and <= 65535 ? port : null;
    }

    /// <summary>
    /// 重建隧道后的解析结果。必须保持稳定身份不变——否则会制造新的登录记录并丢掉保险箱关联。
    /// 端口变化只更新传输地址。
    /// </summary>
    public static ServerConnectionResolution RebindTunnel(
        ServerConnectionResolution previous, int newLocalPort, DateTimeOffset resolvedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (previous.Transport != ServerConnectionTransportKind.SshTunnel)
            throw new InvalidOperationException("Only a tunnel resolution can be rebound to a new local port.");

        // Rebuild through the identity rule so the transport address is normalized exactly like a fresh
        // tunnel resolution. Only the port changes; the serviceId is carried over untouched.
        var identity = ServerConnectionIdentityRules.ManagedTunnel(
            previous.Identity.ServiceId,
            BuildLoopbackBaseUrl(newLocalPort, BasePathOf(previous.Identity.EffectiveBaseUrl)));
        if (!ServerConnectionIdentityRules.PreservesIdentity(previous.Identity, identity))
            throw new InvalidOperationException("Rebinding a tunnel must not change the login identity.");
        return previous with { Identity = identity, LocalPort = newLocalPort, ResolvedAtUtc = resolvedAtUtc };
    }

    /// <summary>直连解析结果：稳定身份与传输地址相同，不使用隧道。</summary>
    public static ServerConnectionResolution Direct(string serverUrl, DateTimeOffset resolvedAtUtc) =>
        new(ServerConnectionIdentityRules.Direct(serverUrl), ServerConnectionTransportKind.Direct, null, resolvedAtUtc);

    /// <summary>受管隧道解析结果：安装标识是稳定身份，loopback 地址只是本次传输地址。</summary>
    public static ServerConnectionResolution ManagedTunnel(
        string installationId, int localPort, string? basePath, DateTimeOffset resolvedAtUtc) =>
        new(
            ServerConnectionIdentityRules.ManagedTunnel(installationId, BuildLoopbackBaseUrl(localPort, basePath)),
            ServerConnectionTransportKind.SshTunnel,
            localPort,
            resolvedAtUtc);

    private static string? BasePathOf(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : null;
}