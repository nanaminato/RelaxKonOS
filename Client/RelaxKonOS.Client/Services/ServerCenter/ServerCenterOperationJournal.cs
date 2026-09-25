using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.ServerCenter;

/// <summary>
/// 一条服务器中心操作的本地记录。它只是**指向权威回执的索引**：宿主上的持久操作记录才是结论来源，
/// 本地记录用于在宿主详情里展示历史，并在断线或客户端重启后按 <c>operationId</c> 重新查询。
/// <para>这里不保存原始命令输出、宿主路径或凭据；只保留可安全展示的短文案与稳定问题码。</para>
/// </summary>
public sealed record ServerCenterOperationRecord(
    Guid OperationId,
    string HostId,
    ServerDeploymentKind Kind,
    ServerDeploymentState State,
    ServerDeploymentPhase Phase,
    long Sequence,
    string? InstallationId,
    string? ProblemCode,
    string? SafeMessage,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    /// <summary>由远端权威回执生成或更新一条本地索引记录。</summary>
    public static ServerCenterOperationRecord From(string hostId, ServerDeploymentOperationDto receipt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(receipt);
        return new ServerCenterOperationRecord(
            receipt.OperationId, hostId, receipt.Kind, receipt.State, receipt.Phase, receipt.Sequence,
            receipt.InstallationId, receipt.ProblemCode, receipt.SafeMessage,
            receipt.StartedAtUtc ?? receipt.TimestampUtc,
            receipt.CompletedAtUtc,
            DateTimeOffset.UtcNow);
    }
}

/// <summary>服务器中心操作记录的本地索引仓库。</summary>
public interface IServerCenterOperationJournal
{
    /// <summary>按宿主读取操作记录，最近的在前。</summary>
    Task<IReadOnlyList<ServerCenterOperationRecord>> LoadAsync(string hostId, CancellationToken cancellationToken = default);

    /// <summary>写入或按 <c>operationId</c> 更新一条记录。</summary>
    Task RecordAsync(ServerCenterOperationRecord record, CancellationToken cancellationToken = default);

    Task<ServerCenterOperationRecord?> FindAsync(Guid operationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// 文件实现：单文件 JSON、原子替换、进程内串行化写入。文件损坏时按「没有历史」处理，
/// 而不是把读不懂的旧文件当作有效回执。
/// </summary>
public sealed class ServerCenterOperationJournal : IServerCenterOperationJournal
{
    /// <summary>每个宿主保留的记录条数上限；超出后丢弃最旧的非进行中记录。</summary>
    public const int MaximumRecordsPerHost = 50;

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ServerCenterOperationJournal(string? directory = null)
    {
        var root = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RelaxKonOS",
            "servercenter");
        _filePath = Path.Combine(root, "operation-journal.json");
    }

    public async Task<IReadOnlyList<ServerCenterOperationRecord>> LoadAsync(
        string hostId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        var records = await LoadAllAsync(cancellationToken).ConfigureAwait(false);
        return records
            .Where(record => string.Equals(record.HostId, hostId, StringComparison.Ordinal))
            .OrderByDescending(record => record.UpdatedAtUtc)
            .ToList();
    }

    public async Task<ServerCenterOperationRecord?> FindAsync(
        Guid operationId, CancellationToken cancellationToken = default)
    {
        var records = await LoadAllAsync(cancellationToken).ConfigureAwait(false);
        return records.FirstOrDefault(record => record.OperationId == operationId);
    }

    public async Task RecordAsync(
        ServerCenterOperationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var remaining = records.Where(item => item.OperationId != record.OperationId).ToList();
            remaining.Add(record);

            var trimmed = new List<ServerCenterOperationRecord>(remaining.Count);
            foreach (var group in remaining.GroupBy(item => item.HostId, StringComparer.Ordinal))
            {
                var ordered = group.OrderByDescending(item => item.UpdatedAtUtc).ToList();
                // An in-flight operation must survive trimming; it is the only handle a later
                // reconnect has for re-reading the authoritative receipt.
                var kept = ordered
                    .Where(item => !ServerDeploymentLifecycle.IsTerminalState(item.State))
                    .Concat(ordered.Where(item => ServerDeploymentLifecycle.IsTerminalState(item.State)))
                    .Take(Math.Max(MaximumRecordsPerHost, ordered.Count(item => !ServerDeploymentLifecycle.IsTerminalState(item.State))))
                    .ToList();
                trimmed.AddRange(kept);
            }

            await WriteUnlockedAsync(trimmed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<ServerCenterOperationRecord>> LoadAllAsync(CancellationToken cancellationToken)
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

    private async Task<IReadOnlyList<ServerCenterOperationRecord>> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            await using var stream = File.OpenRead(_filePath);
            var payload = await JsonSerializer
                .DeserializeAsync<JournalFile>(stream, RelaxKonOSJsonOptions.Default, cancellationToken)
                .ConfigureAwait(false);
            return payload?.Records ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private async Task WriteUnlockedAsync(
        IReadOnlyList<ServerCenterOperationRecord> records, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);

        var temporary = _filePath + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(
                stream, new JournalFile([.. records]), RelaxKonOSJsonOptions.Default, cancellationToken)
                .ConfigureAwait(false);
        File.Move(temporary, _filePath, overwrite: true);
    }

    private sealed record JournalFile(IReadOnlyList<ServerCenterOperationRecord> Records);
}