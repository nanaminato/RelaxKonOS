using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.ServerCenter;

/// <summary>
/// SSH 端点：主机、端口与登录用户。主机密钥与 SSH 凭据都绑定到这个三元组，
/// 因此同一台宿主的两个 SSH 用户不会互相覆盖对方的信任决定。
/// </summary>
public sealed record ServerCenterSshEndpoint(string Host, int Port, string UserName)
{
    /// <summary>主机密钥的端点键 <c>host:port</c>；不含用户，因为主机密钥属于宿主而不是账号。</summary>
    public string EndpointKey => ServerHostTrustRules.EndpointKey(Host, Port);

    /// <summary>展示用名称，永不包含凭据。</summary>
    public string DisplayName => $"{ServerHostTrustRules.NormalizeHost(Host)}:{Port}";

    public static ServerCenterSshEndpoint Create(string host, int port, string userName)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("SSH host is required.", nameof(host));
        if (port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (string.IsNullOrWhiteSpace(userName)) throw new ArgumentException("SSH user is required.", nameof(userName));
        return new ServerCenterSshEndpoint(ServerHostTrustRules.NormalizeHost(host), port, userName.Trim());
    }
}

/// <summary>
/// SSH 认证材料。密码与私钥口令只在本次操作的内存中存在；是否保存由用户显式选择，
/// 且只能进入设备安全存储，绝不进入工作区同步、日志或诊断导出。
/// </summary>
public abstract record ServerCenterSshCredential
{
    /// <summary>SSH 密码。</summary>
    public sealed record Password(string Secret) : ServerCenterSshCredential;

    /// <summary>私钥（PEM/OpenSSH 文本）与可选口令。</summary>
    public sealed record PrivateKey(string PrivateKeyText, string? Passphrase) : ServerCenterSshCredential;
}

/// <summary>一次远端命令的执行结果。原始输出只作为受限诊断附件，默认不展示、不上传。</summary>
public sealed record ServerCenterSshCommandResult(int ExitStatus, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitStatus == 0;
}

/// <summary>
/// 本次连接观测到的宿主密钥。用户核对指纹后由信任仓库固定；指纹不是秘密。
/// </summary>
public sealed record ServerCenterHostKeyObservation(string Host, int Port, string Algorithm, byte[] PublicKeyBlob)
{
    public string Fingerprint => ServerHostTrustRules.Fingerprint(PublicKeyBlob);

    public string GroupedFingerprint => ServerHostTrustRules.GroupedFingerprint(Fingerprint);
}

/// <summary>
/// 主机密钥未被本会话接受。它不是一个可重试的瞬时故障：
/// <see cref="ServerHostKeyTrust.Unknown"/> 需要用户核对指纹，<see cref="ServerHostKeyTrust.Changed"/>
/// 必须阻断写操作并由用户显式更新固定记录。
/// </summary>
public sealed class ServerCenterHostKeyRejectedException(
    ServerCenterHostKeyObservation observation, ServerHostKeyTrust trust)
    : Exception("The SSH host key was not accepted for this session.")
{
    public ServerCenterHostKeyObservation Observation { get; } = observation;

    public ServerHostKeyTrust Trust { get; } = trust;

    public string ProblemCode =>
        ServerHostTrustRules.ProblemCode(Trust) ?? ServerDeploymentProblemCodes.HostKeyUnknown;
}

/// <summary>
/// 一条 loopback 隧道：把远端 loopback 上的服务映射到本机一个由系统分配的端口。
/// 绑定地址固定为 <c>127.0.0.1</c>，不提供改绑到非回环地址的入口。
/// </summary>
public interface IServerCenterSshTunnel : IAsyncDisposable
{
    /// <summary>本机监听端口，由操作系统分配。</summary>
    int LocalPort { get; }

    /// <summary>本次会话使用的基础地址，例如 <c>http://127.0.0.1:51000</c>。</summary>
    string LocalBaseUrl { get; }
}

/// <summary>
/// 客户端内置的 SSH/SFTP 传输。它只提供传输原语；「能执行什么」由服务器中心的操作层用固定枚举约束，
/// 界面不提供自定义远程命令。任何实现都不得调用系统 <c>ssh/scp/sftp</c> 命令作为必需路径。
/// </summary>
public interface IServerCenterSshTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>本次连接观测到的宿主密钥；未连接时为 null。</summary>
    ServerCenterHostKeyObservation? ObservedHostKey { get; }

    /// <summary>
    /// 建立连接。主机密钥在握手阶段核对：<paramref name="hostKeyGuard"/> 只有返回
    /// <see cref="ServerHostKeyTrust.Trusted"/> 才允许继续，否则立即断开并抛出
    /// <see cref="ServerCenterHostKeyRejectedException"/>，绝不以「接受未知密钥」继续。
    /// </summary>
    Task ConnectAsync(
        ServerCenterSshEndpoint endpoint,
        ServerCenterSshCredential credential,
        Func<ServerCenterHostKeyObservation, ServerHostKeyTrust> hostKeyGuard,
        CancellationToken cancellationToken);

    /// <summary>执行一条固定命令并等待结束。</summary>
    Task<ServerCenterSshCommandResult> RunAsync(string command, CancellationToken cancellationToken);

    /// <summary>
    /// 在 PTY 中执行命令，并可写入一行输入。sudo 口令只送入当前交互会话，
    /// 不进入命令行参数、日志或磁盘。
    /// </summary>
    Task<ServerCenterSshCommandResult> RunWithInputAsync(
        string command, string? inputLine, CancellationToken cancellationToken);

    /// <summary>经 SFTP 上传到受控暂存目录。目标路径由操作层给出，不接受用户输入。</summary>
    Task UploadAsync(
        Stream content, string remotePath, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>经 SFTP 下载（用于导出备份）。</summary>
    Task DownloadAsync(string remotePath, Stream destination, CancellationToken cancellationToken);

    /// <summary>
    /// 打开一条到远端 loopback 端口的隧道。<paramref name="remotePort"/> 必须是远端自身的回环端口，
    /// 不接受任意主机名，避免把隧道变成通用的端口转发工具。
    /// </summary>
    IServerCenterSshTunnel OpenLoopbackTunnel(int remotePort, string? basePath = null);
}