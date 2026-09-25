using System.Text.Json;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>Strict wire-shape gate for the minimal Linux launcher before it reads request fields.</summary>
public static class ServerDeploymentRequestWireValidation
{
    private static readonly HashSet<string> RootKeys = ["schemaVersion", "operationId", "kind", "options"];
    private static readonly HashSet<string> OptionKeys =
    [
        "source", "network", "retention", "mode", "version", "packageUri", "stagedPackageName",
        "packageDigest", "expectedInstallationId", "serverPort", "confirmed"
    ];

    public static bool IsStrictRequest(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty || json.Length > 65536 || json.Contains((byte)'\\') || json.Contains((byte)'\n') ||
            json.Contains((byte)'\r')) return false;
        try
        {
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
            {
                MaxDepth = 4, CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            if (!HasOnlyUniqueKeys(document.RootElement, RootKeys)) return false;
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != ServerDeploymentProtocol.Version ||
                !document.RootElement.TryGetProperty("operationId", out var operation) ||
                operation.ValueKind != JsonValueKind.String || !Guid.TryParseExact(operation.GetString(), "D", out _) ||
                !document.RootElement.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
                return false;
            if (document.RootElement.TryGetProperty("options", out var options) &&
                options.ValueKind != JsonValueKind.Null && !HasOnlyUniqueKeys(options, OptionKeys))
                return false;
            if (document.RootElement.TryGetProperty("options", out options) &&
                options.ValueKind == JsonValueKind.Object && !HasValidOptionTypes(options))
                return false;
            return JsonSerializer.Deserialize<ServerDeploymentRequest>(json, RelaxKonOSJsonOptions.Default) is not null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static bool HasOnlyUniqueKeys(JsonElement element, HashSet<string> allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name)) return false;
        return true;
    }

    private static bool HasValidOptionTypes(JsonElement options)
    {
        if (!options.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String ||
            !options.TryGetProperty("network", out var network) || network.ValueKind != JsonValueKind.String)
            return false;
        foreach (var property in options.EnumerateObject())
        {
            var type = property.Value.ValueKind;
            var valid = property.Name switch
            {
                "source" or "network" or "retention" => type == JsonValueKind.String,
                "mode" or "version" or "packageUri" or "stagedPackageName" or
                    "packageDigest" or "expectedInstallationId" =>
                    type is JsonValueKind.String or JsonValueKind.Null,
                "serverPort" => type == JsonValueKind.Null ||
                                type == JsonValueKind.Number && property.Value.TryGetInt32(out _),
                "confirmed" => type is JsonValueKind.True or JsonValueKind.False,
                _ => false
            };
            if (!valid) return false;
        }
        return true;
    }
}
