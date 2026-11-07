using System.Buffers;
using System.Text.Json;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Validates one finite controller snapshot and projects at most its first 2,000 records.</summary>
public sealed class MihomoConnectionSnapshotReader(Action<ProxyConnectionSnapshotEntry>? project, bool statisticsOnly) : IProxyConnectionSnapshotReader
{
    private enum Statistic { None, Upload, Download, Memory }
    private const int MaximumRecordBytes = 256 * 1024;
    private JsonReaderState state;
    private bool rootClosed;
    private bool foundConnections;
    private bool expectConnections;
    private bool inConnections;
    private int records;
    private Statistic nextStatistic;
    private long? upload, download, memory;

    public int Count { get; private set; }
    public ProxyConnectionStatistics Statistics => new(upload!.Value, download!.Value, memory!.Value);

    public long Parse(ReadOnlySequence<byte> buffer, bool final, CancellationToken ct)
    {
        var reader = new Utf8JsonReader(buffer, final, state);
        while (true)
        {
            var before = reader;
            if (!reader.Read()) break;
            ct.ThrowIfCancellationRequested();
            if (reader.CurrentDepth == 0)
            {
                if (reader.TokenType == JsonTokenType.EndObject) rootClosed = true;
                else if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
            }
            else if (nextStatistic != Statistic.None)
            {
                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value) || value < 0)
                    throw new JsonException();
                switch (nextStatistic)
                {
                    case Statistic.Upload when upload is null: upload = value; break;
                    case Statistic.Download when download is null: download = value; break;
                    case Statistic.Memory when memory is null: memory = value; break;
                    default: throw new JsonException();
                }
                nextStatistic = Statistic.None;
            }
            else if (expectConnections)
            {
                expectConnections = false;
                inConnections = reader.TokenType == JsonTokenType.StartArray;
                if (!inConnections && reader.TokenType != JsonTokenType.Null) throw new JsonException();
            }
            else if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("connections"u8))
            {
                if (foundConnections) throw new JsonException();
                foundConnections = expectConnections = true;
            }
            else if (statisticsOnly && reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("uploadTotal"u8)) nextStatistic = Statistic.Upload;
                else if (reader.ValueTextEquals("downloadTotal"u8)) nextStatistic = Statistic.Download;
                else if (reader.ValueTextEquals("memory"u8)) nextStatistic = Statistic.Memory;
            }
            else if (inConnections && reader.CurrentDepth == 2 && (statisticsOnly || records < 2_000))
            {
                if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
                var complete = project is null
                    ? TryReadConnection(ref reader, before.BytesConsumed, ct)
                    : TryProjectConnection(ref reader, before.BytesConsumed);
                if (!complete)
                {
                    reader = before;
                    break;
                }
                records++;
            }
            else if (inConnections && reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.EndArray)
            {
                inConnections = false;
            }
        }
        state = reader.CurrentState;
        if (final && (!rootClosed || !foundConnections || statisticsOnly && (upload is null || download is null || memory is null)))
            throw new JsonException();
        return reader.BytesConsumed;
    }

    private bool TryReadConnection(ref Utf8JsonReader reader, long start, CancellationToken ct)
    {
        var recordDepth = reader.CurrentDepth;
        Utf8JsonReader id = default;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.CurrentDepth == recordDepth && reader.TokenType == JsonTokenType.EndObject)
            {
                if (reader.BytesConsumed - start > MaximumRecordBytes) throw new JsonException();
                if (!statisticsOnly && HasId(ref id, ct)) Count++;
                return true;
            }
            if (statisticsOnly || reader.CurrentDepth != recordDepth + 1 || reader.TokenType != JsonTokenType.PropertyName
                || !reader.ValueTextEquals("id"u8)) continue;
            if (!reader.Read()) return false;
            // Like JsonElement.TryGetProperty, only the last matching value is decoded.
            id = reader;
        }
        return false;
    }

    private static bool HasId(ref Utf8JsonReader reader, CancellationToken ct)
    {
        if (reader.TokenType == JsonTokenType.String) return !string.IsNullOrWhiteSpace(reader.GetString());
        if (reader.TokenType != JsonTokenType.StartArray) return false;
        var arrayDepth = reader.CurrentDepth;
        var seenString = false;
        var hasId = false;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.CurrentDepth == arrayDepth && reader.TokenType == JsonTokenType.EndArray) return hasId;
            if (reader.CurrentDepth != arrayDepth + 1 || reader.TokenType != JsonTokenType.String) continue;
            // The detail projection joins direct string elements with commas.
            var hasText = !string.IsNullOrWhiteSpace(reader.GetString());
            hasId = hasId || seenString || hasText;
            seenString = true;
        }
        throw new JsonException();
    }

    private bool TryProjectConnection(ref Utf8JsonReader reader, long start)
    {
        if (!JsonDocument.TryParseValue(ref reader, out var record)) return false;
        using (record)
        {
            if (reader.BytesConsumed - start > MaximumRecordBytes) throw new JsonException();
            var item = record.RootElement;
            var id = GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) return true;
            Count++;
            var startedAt = item.TryGetProperty("start", out var value) && value.TryGetDateTimeOffset(out var parsed)
                ? parsed : DateTimeOffset.MinValue;
            project!(new(id, GetString(item, "metadata", "network") ?? "unknown", Endpoint(item, false),
                Endpoint(item, true), GetString(item, "rule") ?? "", GetString(item, "chains") ?? "", startedAt));
        }
        return true;
    }

    private static string Endpoint(JsonElement item, bool destination)
    {
        var address = GetString(item, "metadata", destination ? "host" : "sourceIP");
        if (string.IsNullOrWhiteSpace(address))
            address = GetString(item, "metadata", destination ? "destinationIP" : "sourceIP") ?? "";
        var port = GetString(item, "metadata", destination ? "destinationPort" : "sourcePort");
        if (string.IsNullOrWhiteSpace(port)) return address;
        return (address.Contains(':') ? "[" + address + "]" : address) + ":" + port;
    }

    private static string? GetString(JsonElement item, params string[] path)
    {
        foreach (var segment in path)
            if (!item.TryGetProperty(segment, out item)) return null;
        return item.ValueKind == JsonValueKind.String ? item.GetString()
            : item.ValueKind == JsonValueKind.Array ? string.Join(",", item.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()))
            : null;
    }
}
