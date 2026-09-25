using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.ServerCenter;

/// <summary>
/// 已确认的 SSH 主机密钥的本地固定仓库。主机密钥与指纹都不是秘密，因此可以明文保存；
/// 但必须抗意外覆盖，并在变化时阻断所有写操作。
/// </summary>
public interface ISshHostKeyTrustStore
{
    Task<IReadOnlyList<ServerHostKeyRecord>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>判定本次观测到的宿主密钥。首次见到端点或该算法时返回 <see cref="ServerHostKeyTrust.Unknown"/>。</summary>
    Task<ServerHostKeyTrust> EvaluateAsync(
        ServerCenterSshEndpoint endpoint,
        ServerCenterHostKeyObservation observation,
        CancellationToken cancellationToken = default);

    /// <summary>在用户核对指纹后固定该密钥；同一端点同一算法只保留一条记录。</summary>
    Task TrustAsync(
        ServerCenterSshEndpoint endpoint,
        ServerCenterHostKeyObservation observation,
        CancellationToken cancellationToken = default);

    /// <summary>移除某端点某算法的固定记录（用于用户显式解除信任）。</summary>
    Task ForgetAsync(
        string host, int port, string algorithm, CancellationToken cancellationToken = default);

    /// <summary>移除某端点的全部固定记录。</summary>
    Task ForgetEndpointAsync(string host, int port, CancellationToken cancellationToken = default);
}

/// <summary>
/// 文件实现：单文件 JSON、原子替换、进程内串行化写入。文件损坏时按「没有固定记录」处理，
/// 使下一次连接重新走人工核对，而不是静默信任一个读不懂的旧文件。
/// </summary>
public sealed class SshHostKeyTrustStore : ISshHostKeyTrustStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SshHostKeyTrustStore(string? directory = null)
    {
        var root = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RelaxKonOS",
            "servercenter");
        _filePath = Path.Combine(root, "pinned-host-keys.json");
    }

    public async Task<IReadOnlyList<ServerHostKeyRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ServerHostKeyTrust> EvaluateAsync(
        ServerCenterSshEndpoint endpoint,
        ServerCenterHostKeyObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(observation);
        var known = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return ServerHostTrustRules.Evaluate(
            known, endpoint.Host, endpoint.Port, observation.Algorithm, observation.PublicKeyBlob);
    }

    public async Task TrustAsync(
        ServerCenterSshEndpoint endpoint,
        ServerCenterHostKeyObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(observation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var known = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var confirmed = new ServerHostKeyRecord(
                endpoint.Host,
                endpoint.Port,
                observation.Algorithm,
                Convert.ToBase64String(observation.PublicKeyBlob),
                observation.Fingerprint,
                DateTimeOffset.UtcNow);
            await WriteUnlockedAsync(ServerHostTrustRules.Replace(known, confirmed), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ForgetAsync(
        string host, int port, string algorithm, CancellationToken cancellationToken = default)
    {
        var key = ServerHostTrustRules.EndpointKey(host, port);
        await MutateAsync(
            records => records
                .Where(r => !(string.Equals(ServerHostTrustRules.EndpointKey(r.Host, r.Port), key, StringComparison.Ordinal)
                              && string.Equals(r.Algorithm, algorithm, StringComparison.Ordinal)))
                .ToList(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ForgetEndpointAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var key = ServerHostTrustRules.EndpointKey(host, port);
        await MutateAsync(
            records => records
                .Where(r => !string.Equals(ServerHostTrustRules.EndpointKey(r.Host, r.Port), key, StringComparison.Ordinal))
                .ToList(),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task MutateAsync(
        Func<List<ServerHostKeyRecord>, List<ServerHostKeyRecord>> mutate, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var known = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            await WriteUnlockedAsync(mutate([.. known]), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<ServerHostKeyRecord>> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return [];

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var payload = await JsonSerializer
                .DeserializeAsync<PinnedHostKeyCollection>(stream, RelaxKonOSJsonOptions.Default, cancellationToken)
                .ConfigureAwait(false);
            return payload?.Keys ?? [];
        }
        catch (JsonException)
        {
            // A damaged file must not silently trust anything; treat it as "no pinned keys".
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private async Task WriteUnlockedAsync(
        IReadOnlyList<ServerHostKeyRecord> records, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);

        var temporary = _filePath + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(
                stream, new PinnedHostKeyCollection([.. records]), RelaxKonOSJsonOptions.Default, cancellationToken)
                .ConfigureAwait(false);
        File.Move(temporary, _filePath, overwrite: true);
    }

    private sealed record PinnedHostKeyCollection(IReadOnlyList<ServerHostKeyRecord> Keys);
}