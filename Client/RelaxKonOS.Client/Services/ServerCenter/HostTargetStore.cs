using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.ServerCenter;

/// <summary>
/// 本机宿主目标的仓库。宿主目标只包含管理资料（SSH 端点、受管安装标识、最近核验状态），
/// 不含任何凭据，因此可以明文保存；SSH 密码/私钥与主机指纹分别由各自的存储负责。
/// </summary>
public interface IHostTargetStore
{
    Task<IReadOnlyList<ServerHostTarget>> LoadAsync(CancellationToken cancellationToken = default);

    Task<ServerHostTarget?> FindAsync(string hostId, CancellationToken cancellationToken = default);

    /// <summary>按 SSH 端点查找；端点相同即同一目标，与 SSH 用户无关。</summary>
    Task<ServerHostTarget?> FindByEndpointAsync(
        string host, int port, CancellationToken cancellationToken = default);

    /// <summary>写入或更新一个宿主目标。同一端点只会保留一条记录。</summary>
    Task<ServerHostTarget> UpsertAsync(ServerHostTarget target, CancellationToken cancellationToken = default);

    /// <summary>删除一个宿主目标。它只影响本机管理资料：关联的登录与 SSH 凭据分别由用户决定如何处理。</summary>
    Task<bool> RemoveAsync(string hostId, CancellationToken cancellationToken = default);
}

/// <summary>
/// 文件实现：单文件 JSON、原子替换、进程内串行化写入。文件损坏时按「没有宿主目标」处理，
/// 使用户重新添加，而不是让一个读不懂的旧文件继续冒充管理资料。
/// </summary>
public sealed class HostTargetStore : IHostTargetStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HostTargetStore(string? directory = null)
    {
        var root = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RelaxKonOS",
            "servercenter");
        _filePath = Path.Combine(root, "host-targets.json");
    }

    public async Task<IReadOnlyList<ServerHostTarget>> LoadAsync(CancellationToken cancellationToken = default)
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

    public async Task<ServerHostTarget?> FindAsync(string hostId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        var targets = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return targets.FirstOrDefault(t => string.Equals(t.HostId, hostId, StringComparison.Ordinal));
    }

    public async Task<ServerHostTarget?> FindByEndpointAsync(
        string host, int port, CancellationToken cancellationToken = default)
    {
        if (!ServerHostTargetRules.IsValidEndpoint(host, port, "probe"))
            throw new ArgumentException("An SSH host and port are required.", nameof(host));

        var identity = ServerHostTargetRules.EndpointIdentity(host, port);
        var targets = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return targets.FirstOrDefault(t =>
            string.Equals(ServerHostTargetRules.EndpointIdentity(t.SshHost, t.SshPort), identity, StringComparison.Ordinal));
    }

    public async Task<ServerHostTarget> UpsertAsync(
        ServerHostTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!ServerHostTargetRules.IsValidEndpoint(target.SshHost, target.SshPort, target.SshUserName))
            throw new ArgumentException("A host target needs a valid SSH endpoint and user.", nameof(target));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var targets = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var identity = ServerHostTargetRules.EndpointIdentity(target.SshHost, target.SshPort);
            var remaining = targets
                .Where(t => !string.Equals(
                    ServerHostTargetRules.EndpointIdentity(t.SshHost, t.SshPort), identity, StringComparison.Ordinal))
                .ToList();
            remaining.Add(target);
            await WriteUnlockedAsync(remaining, cancellationToken).ConfigureAwait(false);
            return target;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(string hostId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var targets = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var remaining = targets.Where(t => !string.Equals(t.HostId, hostId, StringComparison.Ordinal)).ToList();
            if (remaining.Count == targets.Count) return false;
            await WriteUnlockedAsync(remaining, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<ServerHostTarget>> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return [];

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var payload = await JsonSerializer
                .DeserializeAsync<HostTargetCollection>(stream, RelaxKonOSJsonOptions.Default, cancellationToken)
                .ConfigureAwait(false);
            return payload?.Targets ?? [];
        }
        catch (JsonException)
        {
            // A damaged file must not silently become a host list the user never created.
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private async Task WriteUnlockedAsync(
        IReadOnlyList<ServerHostTarget> targets, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);

        var temporary = _filePath + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(
                stream, new HostTargetCollection([.. targets]), RelaxKonOSJsonOptions.Default, cancellationToken)
                .ConfigureAwait(false);
        File.Move(temporary, _filePath, overwrite: true);
    }

    private sealed record HostTargetCollection(IReadOnlyList<ServerHostTarget> Targets);
}