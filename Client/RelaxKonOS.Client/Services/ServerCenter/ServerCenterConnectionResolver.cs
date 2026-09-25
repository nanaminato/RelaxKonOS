using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.ServerCenter;

/// <summary>创建传输实例。每次会话一条独立连接，由会话负责释放。</summary>
public interface IServerCenterSshTransportFactory
{
    IServerCenterSshTransport Create();
}

/// <summary>默认工厂：内置 SSH.NET 传输，不依赖系统 <c>ssh/scp/sftp</c> 命令。</summary>
public sealed class SshNetServerCenterTransportFactory : IServerCenterSshTransportFactory
{
    public IServerCenterSshTransport Create() => new SshNetServerCenterTransport();
}

/// <summary>
/// 一次宿主会话：一条 SSH 传输加上本次会话的连接解析结果。
/// <para>会话自己持有受管隧道，因此换端口重建时不会遗留旧监听，也不会让旧地址继续被使用。</para>
/// </summary>
public sealed class ServerCenterHostSession : IAsyncDisposable
{
    private readonly IServerCenterSshTransport _transport;
    private IServerCenterSshTunnel? _tunnel;
    private bool _disposed;

    internal ServerCenterHostSession(ServerHostTarget target, IServerCenterSshTransport transport)
    {
        Target = target;
        _transport = transport;
    }

    /// <summary>本次会话对应的宿主目标；其 <see cref="ServerHostTarget.InstallationId"/> 是隧道的稳定身份。</summary>
    public ServerHostTarget Target { get; }

    public IServerCenterSshTransport Transport => _transport;

    /// <summary>握手阶段观测到的宿主密钥；未连接时为 null。</summary>
    public ServerCenterHostKeyObservation? ObservedHostKey => _transport.ObservedHostKey;

    /// <summary>本次会话的解析结果；尚未打开受管隧道时为 null，此时只能做预检与文件上传。</summary>
    public ServerConnectionResolution? Resolution { get; private set; }

    public bool HasTunnel => _tunnel is not null;

    /// <summary>
    /// 打开或重建受管隧道。重建只更新传输地址：<c>serviceId</c> 始终是安装标识，
    /// 因此换端口不会产生新的登录记录，也不会丢掉保险箱关联。
    /// </summary>
    public async Task<ServerConnectionResolution> OpenOrRebindTunnelAsync(
        int remotePort, string? basePath, DateTimeOffset nowUtc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ServerHostTargetRules.HasManagedInstallation(Target))
            throw new InvalidOperationException(
                "A managed loopback tunnel needs a verified installation id; probe the host before tunnelling.");

        var installationId = Target.InstallationId!;
        var previous = Resolution;

        // Open the replacement before closing the incumbent so a failed attempt leaves the session usable.
        var replacement = _transport.OpenLoopbackTunnel(remotePort, basePath);
        var incumbent = _tunnel;
        _tunnel = replacement;

        Resolution = previous is null
            ? ServerTunnelRules.ManagedTunnel(installationId, replacement.LocalPort, basePath, nowUtc)
            : ServerTunnelRules.RebindTunnel(previous, replacement.LocalPort, nowUtc);

        if (incumbent is not null) await incumbent.DisposeAsync().ConfigureAwait(false);
        return Resolution;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        var tunnel = _tunnel;
        _tunnel = null;
        if (tunnel is not null) await tunnel.DisposeAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 连接解析器：把「宿主目标 + SSH 凭据」变成一条可用会话，并把「稳定登录身份」与
/// 「本次会话的 loopback 地址」分开维护。它不替用户决定信任，只把判定所需的固定记录准备好。
/// </summary>
public interface IServerCenterConnectionResolver
{
    /// <summary>直连解析：不建立 SSH，也不产生隧道。稳定身份与传输地址相同。</summary>
    ServerConnectionResolution ResolveDirect(string serverUrl, DateTimeOffset nowUtc);

    /// <summary>
    /// 读出该宿主的固定主机密钥，生成握手期使用的同步守卫。SSH 握手回调是同步的，
    /// 因此候选记录必须在连接前一次读出，判定本身保持纯函数，不在握手线程里做异步 I/O。
    /// </summary>
    Task<Func<ServerCenterHostKeyObservation, ServerHostKeyTrust>> PrepareHostKeyGuardAsync(
        ServerHostTarget target, CancellationToken cancellationToken = default);

    /// <summary>
    /// 建立一条宿主会话。主机密钥未被守卫接受时抛出 <see cref="ServerCenterHostKeyRejectedException"/>，
    /// 绝不静默接受未知密钥。
    /// </summary>
    Task<ServerCenterHostSession> ConnectAsync(
        ServerHostTarget target,
        ServerCenterSshCredential credential,
        Func<ServerCenterHostKeyObservation, ServerHostKeyTrust> hostKeyGuard,
        CancellationToken cancellationToken = default);

    /// <summary>按宿主标识建立会话，并把该宿主的最近使用时间刷新为 <paramref name="nowUtc"/>。</summary>
    Task<ServerCenterHostSession> ConnectAsync(
        string hostId,
        ServerCenterSshCredential credential,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 默认实现：主机密钥固定记录来自本机仓库，宿主目标来自本机仓库，
/// 传输由工厂创建。三者都不含登录凭据，也不与 Workspace 同步。
/// </summary>
public sealed class ServerCenterConnectionResolver(
    ISshHostKeyTrustStore hostKeyTrust,
    IHostTargetStore hostTargets,
    IServerCenterSshTransportFactory transportFactory) : IServerCenterConnectionResolver
{
    public ServerConnectionResolution ResolveDirect(string serverUrl, DateTimeOffset nowUtc) =>
        ServerTunnelRules.Direct(serverUrl, nowUtc);

    public async Task<Func<ServerCenterHostKeyObservation, ServerHostKeyTrust>> PrepareHostKeyGuardAsync(
        ServerHostTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var endpoint = ServerCenterSshEndpoint.Create(target.SshHost, target.SshPort, target.SshUserName);
        var known = await hostKeyTrust.LoadAsync(cancellationToken).ConfigureAwait(false);

        // A first contact stays Unknown and therefore refuses the handshake; the caller must show the
        // fingerprint, have the user confirm it, pin it, and only then reconnect.
        return observation => ServerHostTrustRules.Evaluate(
            known, endpoint.Host, endpoint.Port, observation.Algorithm, observation.PublicKeyBlob);
    }

    public async Task<ServerCenterHostSession> ConnectAsync(
        ServerHostTarget target,
        ServerCenterSshCredential credential,
        Func<ServerCenterHostKeyObservation, ServerHostKeyTrust> hostKeyGuard,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(hostKeyGuard);

        var endpoint = ServerCenterSshEndpoint.Create(target.SshHost, target.SshPort, target.SshUserName);
        var transport = transportFactory.Create();
        try
        {
            await transport.ConnectAsync(endpoint, credential, hostKeyGuard, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new ServerCenterHostSession(target, transport);
    }

    public async Task<ServerCenterHostSession> ConnectAsync(
        string hostId,
        ServerCenterSshCredential credential,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        var target = await hostTargets.FindAsync(hostId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown host target '{hostId}'.");

        var guard = await PrepareHostKeyGuardAsync(target, cancellationToken).ConfigureAwait(false);
        var session = await ConnectAsync(target, credential, guard, cancellationToken).ConfigureAwait(false);

        // The handshake succeeded, so this host was genuinely used; persist that so the host list can
        // order by recency. A failed write must not tear down a working session.
        try
        {
            await hostTargets.UpsertAsync(target with { LastUsedAtUtc = nowUtc }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Usage recency is cosmetic; the connection itself is fine.
        }
        catch (UnauthorizedAccessException)
        {
            // Usage recency is cosmetic; the connection itself is fine.
        }

        return session;
    }
}