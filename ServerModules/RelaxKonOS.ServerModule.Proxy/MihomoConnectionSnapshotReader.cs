using System.Buffers;
using System.Text.Json;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Validates one finite controller snapshot and projects at most its first 2,000 records.</summary>
public sealed class MihomoConnectionSnapshotReader(Action<JsonElement> project) : IProxyConnectionSnapshotReader
{
    private const int MaximumRecordBytes = 256 * 1024;
    private JsonReaderState state;
    private bool rootClosed;
    private bool foundConnections;
    private bool expectConnections;
    private bool inConnections;
    private int records;

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
                    project(record.RootElement);
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
}
