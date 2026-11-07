using System.Buffers;
using System.Text.Json;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Validates one finite controller snapshot and projects at most its first 2,000 records.</summary>
public sealed class MihomoConnectionSnapshotReader(Action<ProxyConnectionSnapshotEntry>? project) : IProxyConnectionSnapshotReader
{
    private const int MaximumRecordBytes = 256 * 1024;
    private JsonReaderState state;
    private bool rootClosed;
    private bool foundConnections;
    private bool expectConnections;
    private bool inConnections;
    private int records;

    public int Count { get; private set; }

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
            else if (inConnections && reader.CurrentDepth == 2 && records < 2_000)
            {
                if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
                if (!JsonDocument.TryParseValue(ref reader, out var record))
                {
                    reader = before;
                    break;
                }
                using (record)
                {
                    if (reader.BytesConsumed - before.BytesConsumed > MaximumRecordBytes) throw new JsonException();
                    ReadConnection(record.RootElement);
                }
                records++;
            }
            else if (inConnections && reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.EndArray)
            {
                inConnections = false;
            }
        }
        state = reader.CurrentState;
        if (final && (!rootClosed || !foundConnections)) throw new JsonException();
        return reader.BytesConsumed;
    }

    private void ReadConnection(JsonElement item)
    {
        var id = GetString(item, "id");
        if (string.IsNullOrWhiteSpace(id)) return;
        Count++;
        if (project is null) return;
        var start = item.TryGetProperty("start", out var value) && value.TryGetDateTimeOffset(out var parsed)
            ? parsed : DateTimeOffset.MinValue;
        project(new(id, GetString(item, "metadata", "network") ?? "unknown", Endpoint(item, false),
            Endpoint(item, true), GetString(item, "rule") ?? "", GetString(item, "chains") ?? "", start));
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
