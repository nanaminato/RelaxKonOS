using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Text.Json;

namespace RelaxKonOS.Protocol.TaskOrchestration;

/// <summary>The only V1 semantic JSON encoding. Set fields are normalized; ordered input arrays are preserved.</summary>
public static class TaskPlanCanonicalizer
{
    public static byte[] Serialize(TaskPlanSemantics semantics) => Canonicalize(JsonSerializer.SerializeToElement(semantics with
    {
        Steps = semantics.Steps.OrderBy(step => step.StepId, StringComparer.Ordinal).Select(step => step with
        {
            DependsOn = Set(step.DependsOn), ResourceKeys = Set(step.ResourceKeys), Problems = Set(step.Problems),
            Baselines = step.Baselines.Distinct().OrderBy(item => item.ResourceKey, StringComparer.Ordinal)
                .ThenBy(item => item.Identity, StringComparer.Ordinal).ThenBy(item => item.Revision).ToImmutableArray(),
            RequiredCapabilities = step.RequiredCapabilities.Distinct().OrderBy(item => item.Capability, StringComparer.Ordinal)
                .ThenBy(item => item.Target, StringComparer.Ordinal).ToImmutableArray()
        }).ToImmutableArray()
    }));
    public static string Digest(TaskPlanSemantics semantics) => Convert.ToHexStringLower(SHA256.HashData(Serialize(semantics)));

    private static ImmutableArray<string> Set(IEnumerable<string> values) => values.Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal).ToImmutableArray();

    public static byte[] Canonicalize(JsonElement value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, value, 0);
        return buffer.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value, int depth)
    {
        if (depth > 32) throw new TaskPlanningException("task.input_depth_exceeded");
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                    throw new TaskPlanningException("task.duplicate_property");
                writer.WriteStartObject();
                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value, depth + 1);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) Write(writer, item, depth + 1);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
            // V1 schemas use signed 64-bit integers only, with no implicit string or float conversion.
            case JsonValueKind.Number when value.TryGetInt64(out var number): writer.WriteNumberValue(number); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new TaskPlanningException("task.input_type_invalid");
        }
    }
}
