using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelaxKonOS.ModulePackaging;

public static class ModulePackageJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            MaxDepth = 32,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
    public static T Read<T>(ReadOnlyMemory<byte> bytes, int limit = 4 * 1024 * 1024)
    {
        if (bytes.Length is 0 || bytes.Length > limit) throw new ModulePackageException("module-package-metadata-size");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            Unique(document.RootElement);
            return JsonSerializer.Deserialize<T>(bytes.Span, Options) ?? throw new JsonException();
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        { throw new ModulePackageException("module-package-metadata-invalid"); }
    }
    private static void Unique(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in node.EnumerateObject())
            { if (!names.Add(item.Name)) throw new JsonException(); Unique(item.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) Unique(item);
    }
}
