using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Server.Tailscale;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(StoredTailscaleOperation))]
[JsonSerializable(typeof(StoredTailscaleLoginOperation))]
internal sealed partial class TailscaleJournalJsonContext : JsonSerializerContext
{
    internal static TailscaleJournalJsonContext Format { get; } = new(new JsonSerializerOptions(RelaxKonOSJsonOptions.Default));
}
