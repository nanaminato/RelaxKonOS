using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelaxKonOS.Protocol.Registry;

namespace RelaxKonOS.Client.Apps.Registry;

/// <summary>JSON navigation is a client view of one versioned registry value, never a new wire path.</summary>
public static class RegistryJsonProjection
{
    public static string NodeLabel(string name, JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? name + " · " + Localization.LocalizedText.Format("registry.tree.items", value.GetArrayLength()) : name;
    public static JsonElement Resolve(JsonElement value, IReadOnlyList<string> path)
    {
        foreach (var part in path)
            value = value.ValueKind == JsonValueKind.Array ? value[int.Parse(part, CultureInfo.InvariantCulture)] : value.GetProperty(part);
        return value;
    }

    public static string Label(string index, JsonElement value)
    {
        string? description = null;
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var field in new[] { "displayName", "name", "title", "key", "id" })
                if (value.TryGetProperty(field, out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString()))
                {
                    description = text.GetString();
                    if (field == "key" && description!.StartsWith("relaxkonos.", StringComparison.Ordinal) && description.Contains(':'))
                        description = description[(description.IndexOf(':') + 1)..];
                    break;
                }
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) description = value.GetRawText();
        if (description?.Length > 60) description = description[..57] + "…";
        return $"[{index}]" + (description is null ? "" : " " + description);
    }

    public static RegistryEntryRow Row(RegistryEntryDto document, string name, IReadOnlyList<string> path)
    {
        var value = Resolve(document.DesiredValue, path);
        var type = value.ValueKind switch
        {
            JsonValueKind.String => RegistryValueType.String,
            JsonValueKind.True or JsonValueKind.False => RegistryValueType.Boolean,
            JsonValueKind.Number => RegistryValueType.Number,
            _ => RegistryValueType.Json,
        };
        var projected = document with
        {
            Name = name, ValueType = type, DesiredValue = value,
            Schema = new(document.Schema.ValueId, document.Schema.CanWrite, false, false, null)
            {
                Editor = type switch
                {
                    RegistryValueType.String => RegistryEditorKind.String,
                    RegistryValueType.Boolean => RegistryEditorKind.Boolean,
                    RegistryValueType.Number => RegistryEditorKind.Number,
                    _ => RegistryEditorKind.Json,
                },
            },
        };
        return RegistryEntryRow.From(projected) with { Document = document, JsonPath = path.ToArray() };
    }

    public static PutRegistryEntryRequest Write(RegistryEntryRow row, JsonElement value)
    {
        var document = row.Document!;
        var root = JsonNode.Parse(document.DesiredValue.GetRawText());
        if (row.JsonPath.Count == 0) root = JsonNode.Parse(value.GetRawText());
        else
        {
            var current = root!;
            foreach (var part in row.JsonPath.SkipLast(1))
                current = current is JsonArray array ? array[int.Parse(part, CultureInfo.InvariantCulture)]! : current[part]!;
            var replacement = JsonNode.Parse(value.GetRawText());
            if (current is JsonArray list) list[int.Parse(row.JsonPath[^1], CultureInfo.InvariantCulture)] = replacement;
            else current[row.JsonPath[^1]] = replacement;
        }
        return new(document.Scope, document.Path, document.Name, document.ValueType, JsonSerializer.SerializeToElement(root), document.Revision);
    }
}
