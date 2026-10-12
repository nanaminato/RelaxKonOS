using System.Text.Json;
using System.Text.Json.Nodes;
using RelaxKonOS.Protocol.Browser;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Registry;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Settings;

namespace RelaxKonOS.Server.ConfigurationRegistry;

/// <summary>Field views of canonical documents; edits atomically replace their one backing revision.</summary>
public static class RegistryConfigurationFields
{
    public static bool ValidExplorerDocument(JsonObject? value) => value is { Count: 6 }
        && value["SortField"] is JsonValue sort && sort.TryGetValue<int>(out var sortValue) && sortValue is >= 0 and <= 3
        && value["ViewMode"] is JsonValue mode && mode.TryGetValue<int>(out var modeValue) && modeValue is >= 0 and <= 3
        && new[] { "SortDescending", "ShowHiddenFiles", "IsCompactView", "FileListCacheEnabled" }
            .All(name => value[name] is JsonValue flag && flag.TryGetValue<bool>(out _));

    public static string? Root(string path) => RegistrySchema.Definitions.Select(definition => definition.Path)
        .FirstOrDefault(root => path == root || path.StartsWith(root + "\\", StringComparison.Ordinal));
    private static JsonObject? ObjectAt(RegistryEntry entry, string path)
    {
        JsonNode? node = JsonNode.Parse(entry.ValueJson);
        foreach (var segment in path[entry.Path.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            node = (node as JsonObject)?[segment];
        return node as JsonObject;
    }
    public static IReadOnlyList<RegistryEntryDto> Values(RegistryEntry entry, string path)
    {
        var schema = RegistrySchema.Find(entry.Scope, entry.Path, entry.Name)!;
        return ObjectAt(entry, path)?.Where(pair => pair.Key != "revision").Select(pair =>
        {
            var value = JsonSerializer.SerializeToElement(pair.Value, RelaxKonOSJsonOptions.Default);
            var type = value.ValueKind switch
            { JsonValueKind.String => RegistryValueType.String, JsonValueKind.Number => RegistryValueType.Number,
              JsonValueKind.True or JsonValueKind.False => RegistryValueType.Boolean, _ => RegistryValueType.Json };
            return new RegistryEntryDto(entry.Scope, path, pair.Key, type, value, entry.Revision, entry.State,
                entry.DesiredUpdatedAt, entry.AppliedAt, schema.ApplyMode, schema.RestartTarget, entry.LastErrorCode, entry.LastErrorMessage);
        }).ToArray() ?? [];
    }
    public static IEnumerable<RegistryKeyDto> Children(RegistryEntry entry, string path) =>
        ObjectAt(entry, path)?.Where(pair => pair.Value is JsonObject && pair.Key != "revision")
            .Select(pair => new RegistryKeyDto(entry.Scope, path + "\\" + pair.Key)) ?? [];
    public static string? Patch(RegistryEntry entry, string path, string name, JsonElement value)
    {
        var root = JsonNode.Parse(entry.ValueJson) as JsonObject;
        JsonObject? node = root;
        foreach (var segment in path[entry.Path.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries)) node = node?[segment] as JsonObject;
        if (node is null || !node.ContainsKey(name) || name == "revision") return null;
        node[name] = JsonNode.Parse(value.GetRawText());
        return Normalize(entry.Path, root!.ToJsonString());
    }

    public static string? Normalize(string path, string json)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        try
        {
            if (path == WorkspaceConfigurationRegistry.ExplorerPath)
                return ValidExplorerDocument(root) ? json : null;
            if (path == WorkspaceConfigurationRegistry.DesktopPath)
                return JsonSerializer.Deserialize<WorkspacePreferencesDto>(json, RelaxKonOSJsonOptions.Default) is { } desktop
                    && WorkspacePreferencesValidator.TryNormalize(desktop, out var desktopNormalized, out _)
                    ? JsonSerializer.Serialize(desktopNormalized with { Revision = null }, RelaxKonOSJsonOptions.Default) : null;
            if (path == WorkspaceConfigurationRegistry.TerminalPath)
                return JsonSerializer.Deserialize<TerminalSettingsDto>(json, RelaxKonOSJsonOptions.Default) is { } terminal
                    && WorkspaceEndpoints.TryNormalize(terminal, out var terminalNormalized)
                    ? JsonSerializer.Serialize(terminalNormalized, RelaxKonOSJsonOptions.Default) : null;
            if (path == WorkspaceConfigurationRegistry.WindowManagerPath)
                return JsonSerializer.Deserialize<WorkspaceWindowLayoutDto>(json, RelaxKonOSJsonOptions.Default) is { } layout
                    && WorkspaceEndpoints.TryNormalize(layout, out var layoutNormalized)
                    ? JsonSerializer.Serialize(layoutNormalized, RelaxKonOSJsonOptions.Default) : null;
            if (path == WorkspaceConfigurationRegistry.BrowserPath)
                return JsonSerializer.Deserialize<BrowserSettingsDto>(json, RelaxKonOSJsonOptions.Default) is { } browser
                    ? JsonSerializer.Serialize(browser.Normalize(), RelaxKonOSJsonOptions.Default) : null;
        }
        catch (JsonException) { }
        return null;
    }
}
