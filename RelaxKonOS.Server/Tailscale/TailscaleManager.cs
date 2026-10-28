using RelaxKonOS.Protocol.Tailscale;
using System.Text.Json;

namespace RelaxKonOS.Server.Tailscale;

public interface ITailscaleManager
{
    Task<TailscaleStatusDto> GetStatusAsync(CancellationToken cancellationToken);
}

/// <summary>Read-only observation of an externally owned local daemon.</summary>
public sealed class TailscaleManager(ITailscaleStatusCommand command) : ITailscaleManager
{
    public async Task<TailscaleStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        var result = await command.ReadAsync(cancellationToken);
        if (result.State != "observed") return new(result.State, ProblemCode: "tailscale." + result.State.Replace('-', '_'));
        try
        {
            using var json = JsonDocument.Parse(result.Json ?? "");
            return ParseStatus(json.RootElement);
        }
        catch (JsonException) { return new("invalid-response", ProblemCode: "tailscale.invalid_response"); }
    }
    public static TailscaleStatusDto ParseStatus(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("BackendState", out var state) || state.ValueKind != JsonValueKind.String)
            return new("invalid-response", ProblemCode: "tailscale.invalid_response");
        static string Text(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        static bool Flag(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        static TailscalePeerDto Peer(JsonElement item) => new(Text(item, "ID"), Text(item, "HostName"),
            Text(item, "DNSName"), item.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array
                ? ips.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [],
            Flag(item, "Online"), Flag(item, "ExitNode"));
        var peers = root.TryGetProperty("Peer", out var map) && map.ValueKind == JsonValueKind.Object
            ? map.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.Object).Select(x => Peer(x.Value))
                .OrderBy(x => x.Id, StringComparer.Ordinal).ToArray() : [];
        return new("observed", Text(root, "Version"), state.GetString(),
            root.TryGetProperty("Self", out var self) && self.ValueKind == JsonValueKind.Object ? Peer(self) : null, peers);
    }
}

