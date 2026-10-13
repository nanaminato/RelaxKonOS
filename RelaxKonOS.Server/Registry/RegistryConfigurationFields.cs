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

    public static string? Root(RegistryScope scope, string path) => RegistrySchema.Definitions.Where(definition => definition.Scope == scope).Select(definition => definition.Path)
        .FirstOrDefault(root => path == root || path.StartsWith(root + "\\", StringComparison.Ordinal));
    private static JsonObject? ObjectAt(RegistryEntry entry, string path)
    {
        JsonNode? node = Document(entry);
        foreach (var segment in path[entry.Path.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            node = (node as JsonObject)?[segment];
        return node as JsonObject;
    }
    public static IReadOnlyList<RegistryEntryDto> Values(RegistryEntry entry, string path)
    {
        var schema = RegistrySchema.Find(entry.Scope, entry.Path, entry.Name)!;
        return ObjectAt(entry, path)?.Where(pair => pair.Key is not ("revision" or "persistedRevision") && pair.Value is not JsonObject).Select(pair =>
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
        ObjectAt(entry, path)?.Where(pair => pair.Value is JsonObject && pair.Key is not ("revision" or "persistedRevision"))
            .Select(pair => new RegistryKeyDto(entry.Scope, path + "\\" + pair.Key)) ?? [];
    public static RegistryValueSchemaDto Describe(RegistryEntryDto value, JsonElement? defaultValue, bool managed)
    {
        var editor = value.ValueType switch
        {
            RegistryValueType.String => RegistryEditorKind.String,
            RegistryValueType.Boolean => RegistryEditorKind.Boolean,
            RegistryValueType.Number => RegistryEditorKind.Number,
            _ => RegistryEditorKind.Json,
        };
        var choices = new List<RegistryValueChoiceDto>();
        void Add(params string[] values) => choices.AddRange(values.Select(item => new RegistryValueChoiceDto(item, JsonSerializer.SerializeToElement(item))));
        if (managed)
        {
            if (value.Path == WorkspaceConfigurationRegistry.DesktopPath)
            {
                if (value.Name == "timeFormat") Add("24h", "12h");
                if (value.Name == "language") Add("follow-system", "zh-CN", "en-US", "ja-JP");
            }
            if (value.Path == WorkspaceConfigurationRegistry.ExplorerPath && value.Name is "SortField" or "ViewMode")
                choices.AddRange(Enumerable.Range(0, 4).Select(index => new RegistryValueChoiceDto(index.ToString(), JsonSerializer.SerializeToElement(index))));
            if (value.Path.EndsWith("\\appearance", StringComparison.Ordinal) && value.Name == "mode") Add("light", "dark");
            if (value.Path == WorkspaceConfigurationRegistry.TerminalPath && value.Name == "cursorStyle") Add("block", "underline", "bar");
            if (value.Path == WorkspaceConfigurationRegistry.TerminalPath && value.Name == "backend") Add(TerminalBackends.All.ToArray());
            if (value.Path == WorkspaceConfigurationRegistry.TerminalPath && value.Name.EndsWith("Color", StringComparison.Ordinal)) editor = RegistryEditorKind.Color;
            if (value.DesiredValue.ValueKind == JsonValueKind.Array && value.DesiredValue.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)
                && value.DesiredValue.GetArrayLength() > 0) editor = RegistryEditorKind.StringList;
        }
        // A declared editor wins over the JSON shape: an empty string list serializes exactly like an
        // empty object list, so the schema — not the payload — decides which control the field gets.
        if (Bounds(value.Path, value.Name) is not null && value.Name != "fontSize") editor = RegistryEditorKind.Integer;
        if (DeclaredEditor(value.Path, value.Name) is { } declared) editor = declared;
        return new(value.Name, CanWrite: true, CanDelete: !managed,
            CanRename: !managed && value.Name != WorkspaceConfigurationRegistry.DefaultValueName, defaultValue)
        {
            Editor = choices.Count > 0 ? RegistryEditorKind.Enum : editor, Choices = choices,
            Minimum = Bounds(value.Path, value.Name)?.Min,
            Maximum = Bounds(value.Path, value.Name)?.Max,
        };
    }

    private static (double Min, double Max)? Bounds(string path, string name) => (path, name) switch
    {
        (WorkspaceConfigurationRegistry.TerminalPath, "fontSize") => (8, 40),
        (WorkspaceConfigurationRegistry.TerminalPath, "scrollbackLines") => (0, 100000),
        (ClientPresentationPaths.Terminal, "paddingTop" or "paddingBottom" or "paddingHorizontal") => (0, 32),
        (ClientPresentationPaths.Registry, "treeRowHeight") => (20, 40),
        (ClientPresentationPaths.Registry, "valueRowHeight") => (22, 44),
        (ClientPresentationPaths.Registry, "iconSize") => (12, 24),
        (ClientPresentationPaths.Registry, "scrollbarThickness") => (4, 12),
        (ClientPresentationPaths.Registry, "sidebarWidth") => (180, 600),
        _ => null,
    };

    /// <summary>Logical editors that a value's JSON shape alone cannot express.</summary>
    private static RegistryEditorKind? DeclaredEditor(string path, string name) => (path, name) switch
    {
        (WorkspaceConfigurationRegistry.DesktopDisplayPath, "visibleAppIds") => RegistryEditorKind.StringList,
        _ => null,
    };
    public static JsonElement? DefaultValue(string root, string path, string name)
    {
        object? defaults = root switch
        {
            WorkspaceConfigurationRegistry.DesktopPath => WorkspacePreferencesDto.Default,
            WorkspaceConfigurationRegistry.TerminalPath => TerminalSettingsDto.Default,
            ClientPresentationPaths.Terminal => TerminalPaddingSettings.Default,
            ClientPresentationPaths.Registry => RegistryAppearanceSettings.Default,
            WorkspaceConfigurationRegistry.ExplorerPath => WorkspaceConfigurationRegistry.ExplorerDefaults,
            WorkspaceConfigurationRegistry.BrowserPath => BrowserSettingsDto.Default,
            WorkspaceConfigurationRegistry.WindowManagerPath => WorkspaceWindowLayoutDto.Default,
            _ => null,
        };
        var entry = new RegistryEntry { Path = root, ValueJson = JsonSerializer.Serialize(defaults, RelaxKonOSJsonOptions.Default) };
        var node = ObjectAt(entry, path);
        return node is not null && node.TryGetPropertyValue(name, out var value)
            ? JsonSerializer.SerializeToElement(value, RelaxKonOSJsonOptions.Default) : null;
    }
    public static string? Patch(RegistryEntry entry, string path, string name, JsonElement value)
    {
        var root = Document(entry);
        JsonObject? node = root;
        foreach (var segment in path[entry.Path.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries)) node = node?[segment] as JsonObject;
        if (node is null || !node.ContainsKey(name) || name == "revision") return null;
        node[name] = JsonNode.Parse(value.GetRawText());
        return Normalize(entry.Path, root!.ToJsonString());
    }

    private static JsonObject? Document(RegistryEntry entry) => entry.Scope == RegistryScope.Workspace && entry.Path == WorkspaceConfigurationRegistry.TerminalPath
        ? JsonSerializer.SerializeToNode(JsonSerializer.Deserialize<TerminalSettingsDto>(entry.ValueJson, RelaxKonOSJsonOptions.Default), RelaxKonOSJsonOptions.Default) as JsonObject
        : JsonNode.Parse(entry.ValueJson) as JsonObject;

    public static string? Normalize(string path, string json)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        try
        {
            if (path == ClientPresentationPaths.Terminal)
                return JsonSerializer.Deserialize<TerminalPaddingSettings>(json, RelaxKonOSJsonOptions.Default) is { IsValid: true } padding ? json : null;
            if (path == ClientPresentationPaths.Registry)
                return JsonSerializer.Deserialize<RegistryAppearanceSettings>(json, RelaxKonOSJsonOptions.Default) is { IsValid: true } appearance ? json : null;
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
