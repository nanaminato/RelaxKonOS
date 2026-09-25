using System.Globalization;

namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>
/// 一次登录连接的稳定身份与当前传输地址。
/// <para><see cref="ServiceId"/> 是稳定身份：直连时是规范化的持久服务器 URL，受管 SSH 隧道时是安装标识。</para>
/// <para><see cref="EffectiveBaseUrl"/> 是本次会话的实际 HTTP 地址：直连时等于持久 URL，隧道时可能是动态选取的
/// loopback 端口。它永远不能作为凭据键，也不得作为长期保存的服务器身份。</para>
/// </summary>
public sealed record ServerConnectionIdentity(
    ServerServiceIdKind Kind,
    string ServiceId,
    string EffectiveBaseUrl);

/// <summary>
/// 连接身份与传输地址的分离规则。登录记录与连接保险箱以 <c>(serviceId, identifier)</c> 为键；
/// 隧道恢复或换端口后只更新 <c>effectiveBaseUrl</c>，绝不创建新的登录记录。
/// </summary>
public static class ServerConnectionIdentityRules
{
    /// <summary>
    /// 规范化服务器 URL：小写 scheme 与 host、去除默认端口、去除末尾斜杠、丢弃查询与片段。
    /// 同一物理服务器的等价写法必须得到同一 <c>serviceId</c>。
    /// </summary>
    public static string NormalizeServerUrl(string serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)) throw new ArgumentException("Server URL is required.", nameof(serverUrl));
        if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var uri))
            throw new ArgumentException("Server URL must be absolute.", nameof(serverUrl));

        var scheme = uri.Scheme.ToLowerInvariant();
        var isDefaultPort = (scheme == "http" && uri.Port == 80) || (scheme == "https" && uri.Port == 443);
        var authority = isDefaultPort
            ? uri.Host.ToLowerInvariant()
            : string.Create(CultureInfo.InvariantCulture, $"{uri.Host.ToLowerInvariant()}:{uri.Port}");
        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{scheme}://{authority}{path}";
    }

    /// <summary>直连登录身份：规范化 URL 即稳定 <c>serviceId</c>。</summary>
    public static ServerConnectionIdentity Direct(string serverUrl)
    {
        var normalized = NormalizeServerUrl(serverUrl);
        return new ServerConnectionIdentity(ServerServiceIdKind.DirectUrl, normalized, normalized);
    }

    /// <summary>
    /// 受管隧道登录身份：安装标识即稳定 <c>serviceId</c>，传输地址是当前 loopback 地址。
    /// 换端口只改变 <paramref name="effectiveBaseUrl"/>。
    /// </summary>
    public static ServerConnectionIdentity ManagedTunnel(string installationId, string effectiveBaseUrl)
    {
        if (!ServerInstallationId.TryNormalize(installationId, out var canonical))
            throw new ArgumentException("Installation id is invalid.", nameof(installationId));
        return new ServerConnectionIdentity(ServerServiceIdKind.ManagedInstallation, canonical, NormalizeServerUrl(effectiveBaseUrl));
    }

    /// <summary>登录记录与保险箱的键。换隧道端口不改变它。</summary>
    public static string CredentialKey(string serviceId, string identifier) => $"{serviceId}\u001f{identifier}";

    /// <summary>
    /// 隧道换端口后身份是否保持不变。客户端在重建隧道后必须满足该条件，否则会制造新的登录记录。
    /// </summary>
    public static bool PreservesIdentity(ServerConnectionIdentity before, ServerConnectionIdentity after) =>
        before.Kind == after.Kind && string.Equals(before.ServiceId, after.ServiceId, StringComparison.Ordinal);
}