using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.EventAlerts;
using RelaxKonOS.Server.Observability;

namespace RelaxKonOS.Server.EventAlerts;

/// <summary>SQLite append-only event ledger plus its transactional alert projection.</summary>
public sealed class EventAlertStore
{
    private readonly string _connectionString;
    private readonly EventAlertsOptions _options;
    private readonly IObservabilitySanitizer _sanitizer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _schemaReady;

    public EventAlertStore(IHostEnvironment environment, EventAlertsOptions options, IObservabilitySanitizer sanitizer)
    {
        _options = options;
        _sanitizer = sanitizer;
        var path = Path.Combine(environment.ContentRootPath, options.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
    }

    internal int MaximumEvidenceLength => _options.MaximumEvidenceLength;
    public event Func<AlertChangedDto, Task>? AlertChanged;
    internal sealed record AppendRequest(string SourceEventKey, EventAlertCatalog.Definition Definition, Guid ResourceId,
        Guid CorrelationId, string ProblemCode, Guid? OperationId, EventAlertSeverity Severity, string? Evidence,
        bool IsRecovery, DateTimeOffset OccurredAt);

    internal async Task AppendAsync(AppendRequest request, CancellationToken ct)
    {
        AlertChangedDto? changed = null;
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var transaction = connection.BeginTransaction();
            if (await ExistsAsync(connection, transaction, "SELECT 1 FROM operational_events WHERE source_event_key=$key", "$key", request.SourceEventKey, ct))
            { await transaction.CommitAsync(ct); return; }

            var eventId = Guid.NewGuid();
            var dedupeKey = request.Definition.Type + ":" + request.Definition.ResourceType + ":" + request.ResourceId.ToString("D");
            var resourceReference = _sanitizer.ToReference(request.ResourceId.ToString("D"));
            await ExecuteAsync(connection, transaction, """
                INSERT INTO operational_events(event_id,source_event_key,occurred_at,type,severity,source,problem_code,correlation_id,operation_id,resource_type,resource_reference,dedupe_key,evidence,target_kind,target_resource_id,is_recovery)
                VALUES($eventId,$sourceKey,$occurredAt,$type,$severity,$source,$problem,$correlation,$operation,$resourceType,$resourceReference,$dedupe,$evidence,$targetKind,$targetResource,$recovery);
                """, ct, ("$eventId", eventId), ("$sourceKey", request.SourceEventKey), ("$occurredAt", Time(request.OccurredAt)),
                ("$type", request.Definition.Type), ("$severity", (int)request.Severity), ("$source", (int)request.Definition.Source),
                ("$problem", request.ProblemCode), ("$correlation", request.CorrelationId), ("$operation", request.OperationId),
                ("$resourceType", request.Definition.ResourceType), ("$resourceReference", resourceReference), ("$dedupe", dedupeKey),
                ("$evidence", request.Evidence), ("$targetKind", (int)request.Definition.TargetKind), ("$targetResource", request.ResourceId), ("$recovery", request.IsRecovery ? 1 : 0));

            var alert = await ReadAlertByKeyAsync(connection, transaction, dedupeKey, ct);
            if (request.IsRecovery)
            {
                if (alert is not null && alert.Status is OperationalAlertStatus.Open or OperationalAlertStatus.Acknowledged)
                    changed = await UpdateAlertAsync(connection, transaction, alert with { Status = OperationalAlertStatus.Resolved, LastEventId = eventId,
                        LastOccurredAt = request.OccurredAt, ProblemCode = request.ProblemCode, ResolutionReason = "source-recovered" }, ct);
            }
            else if (alert is null)
            {
                var fresh = new AlertRow(Guid.NewGuid(), dedupeKey, request.Definition.Type, request.Severity, OperationalAlertStatus.Open,
                    request.OccurredAt, request.OccurredAt, 1, eventId, request.ProblemCode, null, null, null,
                    request.Definition.TargetKind, request.ResourceId, 1);
                await InsertAlertAsync(connection, transaction, fresh, ct);
                changed = ToChanged(fresh);
            }
            else
            {
                var status = alert.Status == OperationalAlertStatus.Resolved ? OperationalAlertStatus.Open : alert.Status;
                var updated = alert with { Severity = Max(alert.Severity, request.Severity), Status = status, LastOccurredAt = request.OccurredAt,
                    OccurrenceCount = checked(alert.OccurrenceCount + 1), LastEventId = eventId, ProblemCode = request.ProblemCode,
                    ResolutionReason = null, Version = alert.Version + 1 };
                changed = await UpdateAlertAsync(connection, transaction, updated, ct);
            }
            await transaction.CommitAsync(ct);
        }
        finally { _gate.Release(); }
        if (changed is not null && AlertChanged is { } notify) await notify(changed);
    }

    public async Task<EventAlertPageDto<OperationalEventDto>> ListEventsAsync(int pageSize, string? cursor, string? type,
        EventAlertSeverity? severity, OperationalEventSource? source, CancellationToken ct)
    {
        pageSize = Math.Clamp(pageSize, 1, _options.MaximumPageSize);
        var marker = DecodeCursor(cursor);
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM operational_events WHERE ($type IS NULL OR type=$type) AND ($severity IS NULL OR severity=$severity) AND ($source IS NULL OR source=$source) AND ($at IS NULL OR occurred_at < $at OR (occurred_at=$at AND event_id < $id)) ORDER BY occurred_at DESC,event_id DESC LIMIT $limit;";
            Bind(command, ("$type", type), ("$severity", severity is null ? null : (int)severity.Value), ("$source", source is null ? null : (int)source.Value),
                ("$at", marker?.At), ("$id", marker?.Id), ("$limit", pageSize + 1));
            var items = new List<OperationalEventDto>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) items.Add(ReadEvent(reader));
            return Page(items, pageSize, item => (item.OccurredAt.UtcDateTime.ToString("O"), item.EventId.ToString("D")));
        }
        finally { _gate.Release(); }
    }

    public async Task<EventAlertPageDto<OperationalAlertDto>> ListAlertsAsync(int pageSize, string? cursor, OperationalAlertStatus? status, EventAlertSeverity? severity, CancellationToken ct)
    {
        pageSize = Math.Clamp(pageSize, 1, _options.MaximumPageSize);
        var marker = DecodeCursor(cursor);
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            // Cursor ordering must exactly match the order clause. Status/severity preference is
            // a presentation concern for the client; putting it before the cursor tuple would
            // make a state transition skip or duplicate rows between pages.
            command.CommandText = "SELECT * FROM operational_alerts WHERE ($status IS NULL OR status=$status) AND ($severity IS NULL OR severity=$severity) AND ($at IS NULL OR last_occurred_at < $at OR (last_occurred_at=$at AND alert_id < $id)) ORDER BY last_occurred_at DESC,alert_id DESC LIMIT $limit;";
            Bind(command, ("$status", status is null ? null : (int)status.Value), ("$severity", severity is null ? null : (int)severity.Value),
                ("$at", marker?.At), ("$id", marker?.Id), ("$limit", pageSize + 1));
            var items = new List<OperationalAlertDto>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) items.Add(ToDto(ReadAlert(reader)));
            return Page(items, pageSize, item => (item.LastOccurredAt.UtcDateTime.ToString("O"), item.AlertId.ToString("D")));
        }
        finally { _gate.Release(); }
    }

    public async Task<OperationalAlertDetailDto?> GetDetailAsync(Guid alertId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            var alert = await ReadAlertAsync(connection, null, alertId, ct);
            if (alert is null) return null;
            var events = await ReadEventsForAlertAsync(connection, alert.DedupeKey, ct);
            var actions = await ReadActionsAsync(connection, alertId, ct);
            return new(ToDto(alert), events, actions);
        }
        finally { _gate.Release(); }
    }

    public async Task<EventAlertSummaryDto> GetSummaryAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT SUM(status=0),SUM(status=1),SUM(status=0 AND severity=2),MAX(CASE WHEN status=0 THEN severity ELSE NULL END),MAX(last_occurred_at) FROM operational_alerts;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return new(ToInt(reader, 0), ToInt(reader, 1), ToInt(reader, 2), reader.IsDBNull(3) ? null : (EventAlertSeverity?)reader.GetInt32(3),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind));
        }
        finally { _gate.Release(); }
    }

    public async Task<OperationalAlertDto?> AcknowledgeAsync(Guid alertId, string actor, string? note, CancellationToken ct) =>
        await MutateAsync(alertId, actor, "acknowledged", note, row => row.Status == OperationalAlertStatus.Open
            ? row with { Status = OperationalAlertStatus.Acknowledged, AcknowledgedAt = DateTimeOffset.UtcNow, AcknowledgedByReference = _sanitizer.ToReference(actor) } : null, ct);

    public async Task<OperationalAlertDto?> ResolveAsync(Guid alertId, string actor, string reason, CancellationToken ct) =>
        await MutateAsync(alertId, actor, "resolved", reason, row => row.Status is OperationalAlertStatus.Open or OperationalAlertStatus.Acknowledged
            ? row with { Status = OperationalAlertStatus.Resolved, ResolutionReason = reason } : null, ct);

    public async Task<OperationalAlertDto?> SuppressAsync(Guid alertId, string actor, string reason, DateTimeOffset expiresAt, CancellationToken ct) =>
        await MutateAsync(alertId, actor, "suppressed", reason, row => row.Status is OperationalAlertStatus.Open or OperationalAlertStatus.Acknowledged
            ? row with { Status = OperationalAlertStatus.Suppressed, ResolutionReason = reason } : null, ct, expiresAt);

    public async Task<OperationalAlertDto?> RemoveSuppressionAsync(Guid alertId, string actor, CancellationToken ct) =>
        await MutateAsync(alertId, actor, "suppression-removed", null, row => row.Status == OperationalAlertStatus.Suppressed
            ? row with { Status = OperationalAlertStatus.Open, ResolutionReason = null } : null, ct);

    public async Task RunRetentionAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            // Expiry restores visibility but does not pretend the underlying source recovered.
            await ExecuteAsync(connection, null, "UPDATE operational_alerts SET status=0,resolution_reason=NULL,version=version+1 WHERE status=3 AND alert_id IN (SELECT alert_id FROM alert_suppressions WHERE expires_at <= $now);", ct, ("$now", Time(DateTimeOffset.UtcNow)));
            await ExecuteAsync(connection, null, "DELETE FROM alert_suppressions WHERE expires_at <= $now;", ct, ("$now", Time(DateTimeOffset.UtcNow)));
            await ExecuteAsync(connection, null, "DELETE FROM operational_events WHERE occurred_at < $cutoff;", ct, ("$cutoff", Time(DateTimeOffset.UtcNow.AddDays(-_options.EventRetentionDays))));
            await ExecuteAsync(connection, null, "DELETE FROM alert_actions WHERE alert_id IN (SELECT alert_id FROM operational_alerts WHERE status=2 AND last_occurred_at < $cutoff);", ct, ("$cutoff", Time(DateTimeOffset.UtcNow.AddDays(-_options.ResolvedAlertRetentionDays))));
            await ExecuteAsync(connection, null, "DELETE FROM operational_alerts WHERE status=2 AND last_occurred_at < $cutoff;", ct, ("$cutoff", Time(DateTimeOffset.UtcNow.AddDays(-_options.ResolvedAlertRetentionDays))));
        }
        finally { _gate.Release(); }
    }

    private async Task<OperationalAlertDto?> MutateAsync(Guid alertId, string actor, string action, string? note, Func<AlertRow, AlertRow?> mutate, CancellationToken ct, DateTimeOffset? suppressionExpiry = null)
    {
        AlertChangedDto? changed = null; OperationalAlertDto? result = null;
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct); await using var transaction = connection.BeginTransaction();
            var row = await ReadAlertAsync(connection, transaction, alertId, ct);
            if (row is null) { await transaction.CommitAsync(ct); return null; }
            var next = mutate(row);
            if (next is null) throw new InvalidOperationException(EventAlertProblemCodes.InvalidTransition);
            next = next with { Version = row.Version + 1 };
            changed = await UpdateAlertAsync(connection, transaction, next, ct);
            await ExecuteAsync(connection, transaction, "INSERT INTO alert_actions(action_id,alert_id,kind,actor_reference,note,created_at) VALUES($id,$alert,$kind,$actor,$note,$at);", ct,
                ("$id", Guid.NewGuid()), ("$alert", alertId), ("$kind", action), ("$actor", _sanitizer.ToReference(actor)), ("$note", note), ("$at", Time(DateTimeOffset.UtcNow)));
            if (suppressionExpiry is not null)
                await ExecuteAsync(connection, transaction, "INSERT INTO alert_suppressions(alert_id,reason,actor_reference,expires_at,created_at) VALUES($alert,$reason,$actor,$expiry,$at) ON CONFLICT(alert_id) DO UPDATE SET reason=excluded.reason,actor_reference=excluded.actor_reference,expires_at=excluded.expires_at,created_at=excluded.created_at;", ct,
                    ("$alert", alertId), ("$reason", note), ("$actor", _sanitizer.ToReference(actor)), ("$expiry", Time(suppressionExpiry.Value)), ("$at", Time(DateTimeOffset.UtcNow)));
            else if (action == "suppression-removed") await ExecuteAsync(connection, transaction, "DELETE FROM alert_suppressions WHERE alert_id=$alert;", ct, ("$alert", alertId));
            await transaction.CommitAsync(ct); result = ToDto(next);
        }
        finally { _gate.Release(); }
        if (changed is not null && AlertChanged is { } notify) await notify(changed);
        return result;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(ct);
        if (!_schemaReady) { await EnsureSchemaAsync(connection, ct); _schemaReady = true; }
        return connection;
    }
    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.CommandText = """
            CREATE TABLE IF NOT EXISTS operational_events(event_id TEXT PRIMARY KEY,source_event_key TEXT NOT NULL UNIQUE,occurred_at TEXT NOT NULL,type TEXT NOT NULL,severity INTEGER NOT NULL,source INTEGER NOT NULL,problem_code TEXT NOT NULL,correlation_id TEXT NOT NULL,operation_id TEXT NULL,resource_type TEXT NOT NULL,resource_reference TEXT NOT NULL,dedupe_key TEXT NOT NULL,evidence TEXT NULL,target_kind INTEGER NOT NULL,target_resource_id TEXT NOT NULL,is_recovery INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_operational_events_cursor ON operational_events(occurred_at DESC,event_id DESC);
            CREATE INDEX IF NOT EXISTS ix_operational_events_dedupe ON operational_events(dedupe_key,occurred_at DESC);
            CREATE TABLE IF NOT EXISTS operational_alerts(alert_id TEXT PRIMARY KEY,dedupe_key TEXT NOT NULL UNIQUE,type TEXT NOT NULL,severity INTEGER NOT NULL,status INTEGER NOT NULL,first_occurred_at TEXT NOT NULL,last_occurred_at TEXT NOT NULL,occurrence_count INTEGER NOT NULL,last_event_id TEXT NOT NULL,problem_code TEXT NOT NULL,acknowledged_at TEXT NULL,acknowledged_by_reference TEXT NULL,resolution_reason TEXT NULL,target_kind INTEGER NOT NULL,target_resource_id TEXT NOT NULL,version INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_operational_alerts_cursor ON operational_alerts(last_occurred_at DESC,alert_id DESC);
            CREATE TABLE IF NOT EXISTS alert_actions(action_id TEXT PRIMARY KEY,alert_id TEXT NOT NULL,kind TEXT NOT NULL,actor_reference TEXT NULL,note TEXT NULL,created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS alert_suppressions(alert_id TEXT PRIMARY KEY,reason TEXT NOT NULL,actor_reference TEXT NOT NULL,expires_at TEXT NOT NULL,created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS event_source_checkpoints(source_name TEXT NOT NULL,source_instance_id TEXT NOT NULL,sequence INTEGER NOT NULL,updated_at TEXT NOT NULL,PRIMARY KEY(source_name,source_instance_id));
            """; await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task<bool> ExistsAsync(SqliteConnection c, SqliteTransaction t, string sql, string name, object value, CancellationToken ct) { await using var cmd = c.CreateCommand(); cmd.Transaction=t; cmd.CommandText=sql; cmd.Parameters.AddWithValue(name,value); return await cmd.ExecuteScalarAsync(ct) is not null; }
    private static async Task ExecuteAsync(SqliteConnection c, SqliteTransaction? t, string sql, CancellationToken ct, params (string, object?)[] values) { await using var cmd=c.CreateCommand(); cmd.Transaction=t; cmd.CommandText=sql; Bind(cmd,values); await cmd.ExecuteNonQueryAsync(ct); }
    private static void Bind(SqliteCommand cmd, params (string, object?)[] values) { foreach(var (name,value) in values) cmd.Parameters.AddWithValue(name,value ?? DBNull.Value); }
    private static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("O");
    private static EventAlertSeverity Max(EventAlertSeverity a, EventAlertSeverity b) => (EventAlertSeverity)Math.Max((int)a,(int)b);
    private static int ToInt(SqliteDataReader r,int ordinal) => r.IsDBNull(ordinal)?0:Convert.ToInt32(r.GetValue(ordinal));
    private static AlertChangedDto ToChanged(AlertRow row) => new(row.AlertId,row.Version,row.Status,row.Severity,row.OccurrenceCount);
    private static OperationalAlertDto ToDto(AlertRow r) => new(r.AlertId,r.Type,r.Severity,r.Status,r.FirstOccurredAt,r.LastOccurredAt,r.OccurrenceCount,r.LastEventId,r.ProblemCode,r.AcknowledgedAt,r.AcknowledgedByReference,r.ResolutionReason,new(r.TargetKind,r.TargetResourceId));
    private static OperationalEventDto ReadEvent(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)),DateTimeOffset.Parse(r.GetString(2),null,System.Globalization.DateTimeStyles.RoundtripKind),r.GetString(3),(EventAlertSeverity)r.GetInt32(4),(OperationalEventSource)r.GetInt32(5),r.GetInt32(15) != 0 ? OperationalEventOutcome.Recovered : OperationalEventOutcome.Failed,r.GetString(6),Guid.Parse(r.GetString(7)),r.IsDBNull(8)?null:Guid.Parse(r.GetString(8)),r.GetString(9),r.GetString(10),r.IsDBNull(12)?null:r.GetString(12),new((RemediationTargetKind)r.GetInt32(13),Guid.Parse(r.GetString(14)),r.IsDBNull(8)?null:Guid.Parse(r.GetString(8))));
    private static AlertRow ReadAlert(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),(EventAlertSeverity)r.GetInt32(3),(OperationalAlertStatus)r.GetInt32(4),DateTimeOffset.Parse(r.GetString(5),null,System.Globalization.DateTimeStyles.RoundtripKind),DateTimeOffset.Parse(r.GetString(6),null,System.Globalization.DateTimeStyles.RoundtripKind),r.GetInt32(7),Guid.Parse(r.GetString(8)),r.GetString(9),r.IsDBNull(10)?null:DateTimeOffset.Parse(r.GetString(10),null,System.Globalization.DateTimeStyles.RoundtripKind),r.IsDBNull(11)?null:r.GetString(11),r.IsDBNull(12)?null:r.GetString(12),(RemediationTargetKind)r.GetInt32(13),Guid.Parse(r.GetString(14)),r.GetInt64(15));
    private static async Task<AlertRow?> ReadAlertByKeyAsync(SqliteConnection c,SqliteTransaction t,string key,CancellationToken ct) { await using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="SELECT * FROM operational_alerts WHERE dedupe_key=$key;";cmd.Parameters.AddWithValue("$key",key);await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?ReadAlert(r):null; }
    private static async Task<AlertRow?> ReadAlertAsync(SqliteConnection c,SqliteTransaction? t,Guid id,CancellationToken ct) { await using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="SELECT * FROM operational_alerts WHERE alert_id=$id;";cmd.Parameters.AddWithValue("$id",id);await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?ReadAlert(r):null; }
    private static async Task InsertAlertAsync(SqliteConnection c,SqliteTransaction t,AlertRow r,CancellationToken ct) => await ExecuteAsync(c,t,"INSERT INTO operational_alerts(alert_id,dedupe_key,type,severity,status,first_occurred_at,last_occurred_at,occurrence_count,last_event_id,problem_code,acknowledged_at,acknowledged_by_reference,resolution_reason,target_kind,target_resource_id,version) VALUES($id,$key,$type,$severity,$status,$first,$last,$count,$event,$problem,$ackAt,$ackBy,$reason,$targetKind,$targetId,$version);",ct,("$id",r.AlertId),("$key",r.DedupeKey),("$type",r.Type),("$severity",(int)r.Severity),("$status",(int)r.Status),("$first",Time(r.FirstOccurredAt)),("$last",Time(r.LastOccurredAt)),("$count",r.OccurrenceCount),("$event",r.LastEventId),("$problem",r.ProblemCode),("$ackAt",r.AcknowledgedAt is null?null:Time(r.AcknowledgedAt.Value)),("$ackBy",r.AcknowledgedByReference),("$reason",r.ResolutionReason),("$targetKind",(int)r.TargetKind),("$targetId",r.TargetResourceId),("$version",r.Version));
    private static async Task<AlertChangedDto> UpdateAlertAsync(SqliteConnection c,SqliteTransaction t,AlertRow r,CancellationToken ct) { await ExecuteAsync(c,t,"UPDATE operational_alerts SET severity=$severity,status=$status,last_occurred_at=$last,occurrence_count=$count,last_event_id=$event,problem_code=$problem,acknowledged_at=$ackAt,acknowledged_by_reference=$ackBy,resolution_reason=$reason,version=$version WHERE alert_id=$id;",ct,("$id",r.AlertId),("$severity",(int)r.Severity),("$status",(int)r.Status),("$last",Time(r.LastOccurredAt)),("$count",r.OccurrenceCount),("$event",r.LastEventId),("$problem",r.ProblemCode),("$ackAt",r.AcknowledgedAt is null?null:Time(r.AcknowledgedAt.Value)),("$ackBy",r.AcknowledgedByReference),("$reason",r.ResolutionReason),("$version",r.Version)); return ToChanged(r); }
    private static async Task<IReadOnlyList<OperationalEventDto>> ReadEventsForAlertAsync(SqliteConnection c,string key,CancellationToken ct) { await using var cmd=c.CreateCommand();cmd.CommandText="SELECT * FROM operational_events WHERE dedupe_key=$key ORDER BY occurred_at DESC,event_id DESC LIMIT 100;";cmd.Parameters.AddWithValue("$key",key);await using var r=await cmd.ExecuteReaderAsync(ct);var list=new List<OperationalEventDto>();while(await r.ReadAsync(ct))list.Add(ReadEvent(r));return list; }
    private static async Task<IReadOnlyList<AlertActionDto>> ReadActionsAsync(SqliteConnection c,Guid id,CancellationToken ct) { await using var cmd=c.CreateCommand();cmd.CommandText="SELECT action_id,kind,actor_reference,note,created_at FROM alert_actions WHERE alert_id=$id ORDER BY created_at DESC LIMIT 100;";cmd.Parameters.AddWithValue("$id",id);await using var r=await cmd.ExecuteReaderAsync(ct);var list=new List<AlertActionDto>();while(await r.ReadAsync(ct))list.Add(new(Guid.Parse(r.GetString(0)),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),DateTimeOffset.Parse(r.GetString(4),null,System.Globalization.DateTimeStyles.RoundtripKind)));return list; }
    private static EventAlertPageDto<T> Page<T>(List<T> all,int size,Func<T,(string,string)> marker) { var has=all.Count>size; var items=all.Take(size).ToArray(); if(!has)return new(items,null);var (at,id)=marker(items[^1]);return new(items,Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(at+"|"+id))); }
    private static (string At,string Id)? DecodeCursor(string? cursor) { if(string.IsNullOrWhiteSpace(cursor))return null;try {var value=System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));var p=value.Split('|');if(p.Length!=2||!DateTimeOffset.TryParse(p[0],out _)||!Guid.TryParse(p[1],out _))throw new FormatException();return(p[0],p[1]);}catch{throw new ArgumentException(EventAlertProblemCodes.InvalidCursor);}}
    private sealed record AlertRow(Guid AlertId,string DedupeKey,string Type,EventAlertSeverity Severity,OperationalAlertStatus Status,DateTimeOffset FirstOccurredAt,DateTimeOffset LastOccurredAt,int OccurrenceCount,Guid LastEventId,string ProblemCode,DateTimeOffset? AcknowledgedAt,string? AcknowledgedByReference,string? ResolutionReason,RemediationTargetKind TargetKind,Guid TargetResourceId,long Version);
}
