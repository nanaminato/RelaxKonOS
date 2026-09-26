using System.Text.Json;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>
/// 解析部署启动器的持久回执与事件流。断线恢复以回执为准；调用方不能从 SSH 退出码或
/// 最后一帧事件推断成功。所有输入都有大小上限，避免远端异常输出耗尽客户端内存。
/// </summary>
public static class ServerDeploymentRecordReader
{
    public const int MaximumRecordBytes = 1024 * 1024;
    public const int MaximumEventStreamBytes = 8 * 1024 * 1024;

    public static ServerDeploymentOperationDto ReadRecord(string json, Guid expectedOperationId)
    {
        if (string.IsNullOrWhiteSpace(json) || System.Text.Encoding.UTF8.GetByteCount(json) > MaximumRecordBytes)
            throw InvalidRecord("operation record is empty or too large");

        ServerDeploymentOperationDto? record;
        try
        {
            record = JsonSerializer.Deserialize<ServerDeploymentOperationDto>(json, RelaxKonOSJsonOptions.Default);
        }
        catch (JsonException error)
        {
            throw InvalidRecord("operation record is not valid JSON", error);
        }

        if (record is null || record.SchemaVersion != ServerDeploymentProtocol.Version
            || record.OperationId != expectedOperationId || record.Sequence < 0
            || record.TimestampUtc == default || record.Progress is < 0 or > 100
            || (record.InstallationId is not null && !ServerInstallationId.IsValid(record.InstallationId))
            || record.Cancellable != ServerDeploymentLifecycle.IsCancellable(record.Phase, record.State))
            throw InvalidRecord("operation record violates the deployment contract");

        if (record.State == ServerDeploymentState.Succeeded)
        {
            if (record.Phase != ServerDeploymentPhase.Completed)
                throw InvalidRecord("successful operation has no completed phase");
            if (record.Kind is ServerDeploymentKind.Install or ServerDeploymentKind.Upgrade
                or ServerDeploymentKind.Repair or ServerDeploymentKind.Rollback)
            {
                if (record.Result is not { Healthy: true } result
                    || !ServerInstallationId.IsValid(result.InstallationId)
                    || !ServerDeploymentInputRules.IsVersion(result.Version))
                    throw InvalidRecord("successful deployment lacks verified installation evidence");
            }
            if (record.Kind == ServerDeploymentKind.Uninstall && record.Result?.DataRetained is null)
                throw InvalidRecord("successful uninstall lacks a data retention receipt");
            if (record.Kind == ServerDeploymentKind.Probe && record.Probe is null)
                throw InvalidRecord("successful probe lacks host facts");
            if (record.Kind == ServerDeploymentKind.Status && record.Snapshot is null)
                throw InvalidRecord("successful status lacks a verified snapshot");
        }

        return record;
    }

    public static IReadOnlyList<ServerDeploymentEventDto> ReadEvents(
        string jsonLines, Guid expectedOperationId, long afterSequence = 0)
    {
        if (afterSequence < 0 || System.Text.Encoding.UTF8.GetByteCount(jsonLines) > MaximumEventStreamBytes)
            throw InvalidRecord("event stream is too large or sequence is invalid");

        var events = new List<ServerDeploymentEventDto>();
        long previousSequence = 0;
        ServerDeploymentPhase? previousPhase = null;
        ServerDeploymentKind? previousKind = null;
        ServerDeploymentState? previousState = null;
        string? establishedInstallationId = null;
        foreach (var line in jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ServerDeploymentEventDto? current;
            try
            {
                current = JsonSerializer.Deserialize<ServerDeploymentEventDto>(line, RelaxKonOSJsonOptions.Default);
            }
            catch (JsonException error)
            {
                throw InvalidRecord("event stream contains invalid JSON", error);
            }

            if (current is null || current.SchemaVersion != ServerDeploymentProtocol.Version
                || current.OperationId != expectedOperationId || current.Sequence <= 0
                || current.TimestampUtc == default || current.Progress is < 0 or > 100
                || (current.InstallationId is not null && !ServerInstallationId.IsValid(current.InstallationId)))
                throw InvalidRecord("event stream violates the deployment contract");

            if (previousPhase is not null && ServerDeploymentLifecycle.ValidateEventProgress(
                    previousPhase.Value, previousSequence, current.Phase, current.Sequence) is not null)
                throw InvalidRecord("event stream is not monotonic");
            if (previousKind is not null && previousKind != current.Kind)
                throw InvalidRecord("event stream changes operation kind");
            if (previousState is not null && ServerDeploymentLifecycle.IsTerminalState(previousState.Value))
                throw InvalidRecord("event stream continues after a terminal state");
            if (establishedInstallationId is not null && current.InstallationId != establishedInstallationId)
                throw InvalidRecord("event stream changes installation identity");

            previousPhase = current.Phase;
            previousKind = current.Kind;
            previousState = current.State;
            establishedInstallationId ??= current.InstallationId;
            previousSequence = current.Sequence;
            if (current.Sequence > afterSequence) events.Add(current);
        }

        return events;
    }

    private static InvalidDataException InvalidRecord(string message, Exception? inner = null) =>
        new($"{ServerDeploymentProblemCodes.InvalidRequest}: {message}", inner);
}
