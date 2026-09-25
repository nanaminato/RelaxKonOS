using System.Text;
using RelaxKonOS.Protocol.ServerCenter;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RelaxKonOS.Client.Services.ServerCenter;

/// <summary>
/// 基于内置 SSH.NET 的传输实现。它不调用系统 <c>ssh/scp/sftp</c> 命令，因此全新设备无需另装 SSH 工具。
/// <para>主机密钥在握手回调中判定：只有守卫返回 <see cref="ServerHostKeyTrust.Trusted"/> 才继续。
/// 不存在等价于 <c>StrictHostKeyChecking=no</c> 的静默接受路径。</para>
/// </summary>
public sealed class SshNetServerCenterTransport : IServerCenterSshTransport
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ForwardedPortLocal> _tunnels = [];

    private ConnectionInfo? _connectionInfo;
    private SshClient? _client;
    private SftpClient? _sftp;
    private ServerCenterSshEndpoint? _endpoint;
    private ServerCenterHostKeyObservation? _observedHostKey;
    private ServerHostKeyTrust _lastTrust;
    private bool _disposed;

    public bool IsConnected => _client?.IsConnected == true;

    public ServerCenterHostKeyObservation? ObservedHostKey => _observedHostKey;

    public async Task ConnectAsync(
        ServerCenterSshEndpoint endpoint,
        ServerCenterSshCredential credential,
        Func<ServerCenterHostKeyObservation, ServerHostKeyTrust> hostKeyGuard,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(hostKeyGuard);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected && _endpoint == endpoint) return;
            DisconnectUnlocked();

            _endpoint = endpoint;
            _connectionInfo = BuildConnectionInfo(endpoint, credential);
            _observedHostKey = null;
            _lastTrust = ServerHostKeyTrust.Unknown;

            var client = new SshClient(_connectionInfo);
            client.HostKeyReceived += (_, args) =>
            {
                var observation = new ServerCenterHostKeyObservation(
                    endpoint.Host, endpoint.Port, args.HostKeyName, (byte[])args.HostKey.Clone());
                _observedHostKey = observation;
                _lastTrust = hostKeyGuard(observation);
                args.CanTrust = _lastTrust == ServerHostKeyTrust.Trusted;
            };

            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (_observedHostKey is not null && _lastTrust != ServerHostKeyTrust.Trusted)
            {
                // SSH.NET reports a rejected host key as a connection failure; translate it back into the
                // trust decision so the caller can drive the fingerprint confirmation flow.
                client.Dispose();
                throw new ServerCenterHostKeyRejectedException(_observedHostKey, _lastTrust);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            _client = client;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ServerCenterSshCommandResult> RunAsync(string command, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return Task.Run(() =>
        {
            using var sshCommand = Client.CreateCommand(command);
            sshCommand.Execute();
            return new ServerCenterSshCommandResult(sshCommand.ExitStatus ?? -1, sshCommand.Result, sshCommand.Error);
        }, cancellationToken);
    }

    /// <summary>
    /// 通过命令的标准输入送入一行输入，用于 <c>sudo -S</c> 之类的受控提权。口令不进入命令行参数，
    /// 也不回显到 PTY，因此不会出现在进程列表、日志或磁盘上。
    /// </summary>
    public Task<ServerCenterSshCommandResult> RunWithInputAsync(
        string command, string? inputLine, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return Task.Run(() =>
        {
            using var sshCommand = Client.CreateCommand(command);
            using var input = sshCommand.CreateInputStream();
            var execution = sshCommand.BeginExecute();
            if (!string.IsNullOrEmpty(inputLine))
            {
                var bytes = Encoding.UTF8.GetBytes(inputLine + "\n");
                input.Write(bytes, 0, bytes.Length);
                input.Flush();
            }

            sshCommand.EndExecute(execution);
            return new ServerCenterSshCommandResult(sshCommand.ExitStatus ?? -1, sshCommand.Result, sshCommand.Error);
        }, cancellationToken);
    }

    public async Task UploadAsync(
        Stream content, string remotePath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        var sftp = await GetSftpAsync(cancellationToken).ConfigureAwait(false);
        var total = content.CanSeek ? content.Length : 0;

        await Task.Run(() =>
        {
            sftp.UploadFile(content, remotePath, uploaded =>
            {
                if (progress is null) return;
                // Without a reliable denominator the UI must not invent a percentage.
                if (total > 0) progress.Report(Math.Clamp((double)uploaded / total, 0d, 1d));
            });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadAsync(string remotePath, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentNullException.ThrowIfNull(destination);
        var sftp = await GetSftpAsync(cancellationToken).ConfigureAwait(false);
        await Task.Run(() => sftp.DownloadFile(remotePath, destination, null), cancellationToken).ConfigureAwait(false);
    }

    public IServerCenterSshTunnel OpenLoopbackTunnel(int remotePort, string? basePath = null)
    {
        if (remotePort is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(remotePort));
        if (!ServerTunnelRules.IsAcceptableTunnelBindAddress(ServerTunnelRules.LoopbackHost))
            throw new InvalidOperationException("The tunnel bind address must be loopback.");

        var port = new ForwardedPortLocal(
            ServerTunnelRules.LoopbackHost,
            ServerTunnelRules.EphemeralPort,
            ServerTunnelRules.LoopbackHost,
            (uint)remotePort);
        Client.AddForwardedPort(port);
        port.Start();
        _tunnels.Add(port);
        return new SshNetLoopbackTunnel(this, port, (int)port.BoundPort, basePath);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            DisconnectUnlocked();
        }
        finally
        {
            _gate.Release();
        }
        _gate.Dispose();
    }

    internal void CloseTunnel(ForwardedPortLocal port)
    {
        _tunnels.Remove(port);
        try
        {
            if (port.IsStarted) port.Stop();
        }
        catch (Exception)
        {
            // A tunnel that is already gone is not an error worth surfacing.
        }

        try
        {
            _client?.RemoveForwardedPort(port);
        }
        catch (Exception)
        {
            // The client may already be disconnected; the port is unusable either way.
        }

        port.Dispose();
    }

    private SshClient Client => _client is { IsConnected: true }
        ? _client
        : throw new InvalidOperationException("The SSH connection is not established.");

    private async Task<SftpClient> GetSftpAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sftp is { IsConnected: true }) return _sftp;
            var info = _connectionInfo ?? throw new InvalidOperationException("The SSH connection is not established.");
            var pinned = _observedHostKey;
            if (!IsConnected || pinned is null)
                throw new InvalidOperationException("A trusted SSH connection is required before opening SFTP.");

            // SSH.NET opens a second SSH connection for SFTP. That handshake must be pinned as well:
            // trusting only the command connection would let DNS or a network change redirect uploads.
            var sftp = new SftpClient(info);
            ServerCenterHostKeyObservation? rejected = null;
            sftp.HostKeyReceived += (_, args) =>
            {
                args.CanTrust = string.Equals(args.HostKeyName, pinned.Algorithm, StringComparison.Ordinal)
                    && args.HostKey.AsSpan().SequenceEqual(pinned.PublicKeyBlob);
                if (!args.CanTrust)
                    rejected = new ServerCenterHostKeyObservation(
                        pinned.Host, pinned.Port, args.HostKeyName, (byte[])args.HostKey.Clone());
            };

            try
            {
                await sftp.ConnectAsync(cancellationToken).ConfigureAwait(false);
                _sftp = sftp;
                return sftp;
            }
            catch (Exception) when (rejected is not null)
            {
                sftp.Dispose();
                throw new ServerCenterHostKeyRejectedException(rejected, ServerHostKeyTrust.Changed);
            }
            catch
            {
                sftp.Dispose();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void DisconnectUnlocked()
    {
        foreach (var port in _tunnels.ToArray()) CloseTunnel(port);
        _tunnels.Clear();

        _sftp?.Dispose();
        _sftp = null;
        _client?.Dispose();
        _client = null;
        _connectionInfo = null;
    }

    private static ConnectionInfo BuildConnectionInfo(
        ServerCenterSshEndpoint endpoint, ServerCenterSshCredential credential)
    {
        AuthenticationMethod method = credential switch
        {
            ServerCenterSshCredential.Password password =>
                new PasswordAuthenticationMethod(endpoint.UserName, password.Secret),
            ServerCenterSshCredential.PrivateKey key => new PrivateKeyAuthenticationMethod(
                endpoint.UserName,
                new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(key.PrivateKeyText)), key.Passphrase)),
            _ => throw new ArgumentOutOfRangeException(nameof(credential))
        };

        return new ConnectionInfo(endpoint.Host, endpoint.Port, endpoint.UserName, method)
        {
            Timeout = ConnectTimeout
        };
    }
}

/// <summary>
/// SSH.NET 的 loopback 隧道句柄。它只暴露本机端口与基础地址；远端目标固定为远端自身的
/// <c>127.0.0.1</c>，因此不能把隧道当成通用端口转发工具使用。
/// </summary>
internal sealed class SshNetLoopbackTunnel(
    SshNetServerCenterTransport owner, ForwardedPortLocal port, int localPort, string? basePath)
    : IServerCenterSshTunnel
{
    private bool _disposed;

    public int LocalPort { get; } = localPort;

    public string LocalBaseUrl { get; } = ServerTunnelRules.BuildLoopbackBaseUrl(localPort, basePath);

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        owner.CloseTunnel(port);
        return ValueTask.CompletedTask;
    }
}
