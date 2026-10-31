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
        static IReadOnlyList<string> Strings(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [];
        static DateTimeOffset? Time(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) && parsed.Year > 1 ? parsed : null;
        static ulong Id(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var id) ? id : 0;

        // UserID references are numeric; the profile table is keyed by the same numbers as JSON strings.
        var users = new Dictionary<ulong, (string Login, string Display)>();
        if (root.TryGetProperty("User", out var userMap) && userMap.ValueKind == JsonValueKind.Object)
            foreach (var user in userMap.EnumerateObject())
                if (user.Value.ValueKind == JsonValueKind.Object && ulong.TryParse(user.Name, out var userId) && userId != 0)
                    users[userId] = (Text(user.Value, "LoginName"), Text(user.Value, "DisplayName"));

        TailscalePeerDto Peer(JsonElement item)
        {
            var userId = Id(item, "UserID");
            var owner = users.TryGetValue(userId, out var profile) ? profile.Login : null;
            return new(Text(item, "ID"), Text(item, "HostName"), Text(item, "DNSName"), Strings(item, "TailscaleIPs"),
                Flag(item, "Online"), Flag(item, "ExitNode"), Text(item, "OS") is { Length: > 0 } os ? os : null,
                Strings(item, "Tags") is { Count: > 0 } tags ? tags : null, Flag(item, "ExitNodeOption"),
                Flag(item, "Expired"), Time(item, "KeyExpiry"), Time(item, "LastSeen"), owner);
        }
        var peers = root.TryGetProperty("Peer", out var map) && map.ValueKind == JsonValueKind.Object
            ? map.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.Object).Select(x => Peer(x.Value))
                .OrderBy(x => x.Id, StringComparer.Ordinal).ToArray() : [];
        var self = root.TryGetProperty("Self", out var selfElement) && selfElement.ValueKind == JsonValueKind.Object ? Peer(selfElement) : null;
        var account = self is not null && users.TryGetValue(Id(selfElement, "UserID"), out var selfProfile)
            ? new TailscaleAccountDto(selfProfile.Login, selfProfile.Display) : null;
        var tailnet = root.TryGetProperty("CurrentTailnet", out var tailnetElement) && tailnetElement.ValueKind == JsonValueKind.Object
            ? Text(tailnetElement, "Name") : "";
        var suffix = root.TryGetProperty("CurrentTailnet", out var suffixElement) && suffixElement.ValueKind == JsonValueKind.Object
            ? Text(suffixElement, "MagicDNSSuffix") : Text(root, "MagicDNSSuffix");
        TailscalePeerDto? exitNode = null;
        if (root.TryGetProperty("ExitNodeStatus", out var exitElement) && exitElement.ValueKind == JsonValueKind.Object)
        {
            var exitId = Text(exitElement, "ID");
            exitNode = peers.FirstOrDefault(peer => peer.Id == exitId);
        }
        return new("observed", Text(root, "Version"), state.GetString(), self, peers,
            TailnetName: tailnet.Length > 0 ? tailnet : null,
            MagicDnsSuffix: suffix.Length > 0 ? suffix : null,
            Account: account is { LoginName.Length: > 0 } ? account : null,
            ExitNode: exitNode);
    }
}
