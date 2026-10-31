using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.Protocol.Tailscale;
using RelaxKonOS.Server.Tailscale;

namespace RelaxKonOS.PrivilegedHelper;

internal interface ITailscaleNativeControl
{
    Task<TailscaleControlSnapshot> ReadAsync(CancellationToken ct);
    Task<bool> IsQuiescentAsync(CancellationToken ct);
    Task<bool> ApplyAsync(TailscalePrepareRequest request, CancellationToken ct);
}

/// <summary>Uses only the documented fixed CLI operations; native preference output never leaves Helper.</summary>
internal sealed class TailscaleNativeControl : ITailscaleNativeControl
{
    public async Task<TailscaleControlSnapshot> ReadAsync(CancellationToken ct)
    {
        TailscaleControlProtection.RequireExecutable();
        var before = await TailscaleStatusCommand.RunFixedAsync(["status", "--json"], ct);
        var prefs = await TailscaleStatusCommand.RunFixedAsync(["get", "--json"], ct);
        var after = await TailscaleStatusCommand.RunFixedAsync(["status", "--json"], ct);
        if (before.State != "observed" || prefs.State != "observed" || after.State != "observed")
            throw new IOException("tailscale.control_unavailable");
        return Parse(before.Json!, prefs.Json!, after.Json!);
    }

    internal static TailscaleControlSnapshot Parse(string beforeJson, string prefsJson, string afterJson)
    {
        using var before = JsonDocument.Parse(beforeJson);
        using var after = JsonDocument.Parse(afterJson);
        using var prefs = JsonDocument.Parse(prefsJson);
        static string Identity(JsonElement root)
        {
            var state = root.GetProperty("BackendState").GetString();
            var hasSelf = root.TryGetProperty("Self", out var self) && self.ValueKind == JsonValueKind.Object;
            var id = hasSelf && self.TryGetProperty("ID", out var identity) ? identity.GetString() : "";
            var version = root.GetProperty("Version").GetString();
            if (state is not ("Running" or "Stopped" or "NeedsLogin" or "NeedsMachineAuth" or "Starting") || string.IsNullOrWhiteSpace(version))
                throw new IOException("tailscale.control_unavailable");
            // A signed-in node with an open challenge or an expired key cannot be controlled; a
            // logged-out node is still a valid observation for the explicit logout operation.
            if (state is "Running" or "Stopped"
                && (root.TryGetProperty("AuthURL", out var auth) && auth.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(auth.GetString())
                    || hasSelf && self.TryGetProperty("Expired", out var expired) && expired.ValueKind == JsonValueKind.True))
                throw new IOException("tailscale.login_required");
            return JsonSerializer.Serialize(new { state, id, version });
        }
        var identity = Identity(before.RootElement);
        if (identity != Identity(after.RootElement)) throw new IOException("tailscale.observation_changed");
        var settings = prefs.RootElement;
        if (settings.ValueKind != JsonValueKind.Object) throw new IOException("tailscale.invalid_preferences");
        // Reject duplicate properties and normalize ordering for a stable, complete preference revision.
        var canonical = Canonical(settings);
        var revision = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity + "\n" + canonical)));
        static bool Bool(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new IOException("tailscale.invalid_preferences")
            } : false;
        static string? ExitNode(JsonElement item)
        {
            if (!item.TryGetProperty("exit-node", out var value)) return null;
            if (value.ValueKind != JsonValueKind.String) throw new IOException("tailscale.invalid_preferences");
            var text = value.GetString();
            if (string.IsNullOrEmpty(text)) return null;
            // Keep "auto:any" as an opaque display value; mutations only ever send a concrete peer IP.
            if (text == "auto:any") return text;
            return IPAddress.TryParse(text, out var address) ? address.ToString() : throw new IOException("tailscale.invalid_preferences");
        }
        var dns = settings.GetProperty("accept-dns").GetBoolean();
        var routes = settings.GetProperty("accept-routes").GetBoolean();
        var preferences = new TailscalePreferences(dns, routes, Bool(settings, "shields-up"), ExitNode(settings),
            Bool(settings, "exit-node-allow-lan-access"), Bool(settings, "unattended"), Bool(settings, "auto-update"),
            Bool(settings, "advertise-exit-node"));
        var backendState = before.RootElement.GetProperty("BackendState").GetString()!;
        var authenticated = backendState is "Running" or "Stopped";
        var identityId = before.RootElement.TryGetProperty("Self", out var selfElement) && selfElement.ValueKind == JsonValueKind.Object
            && selfElement.TryGetProperty("ID", out var idElement) ? idElement.GetString() : "";
        return new(revision, backendState == "Running", authenticated,
            new(dns, routes), authenticated && !string.IsNullOrWhiteSpace(identityId), Preferences: preferences,
            Platform: OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unsupported");
    }

    internal static string Canonical(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Count() == 1
                ? JsonSerializer.Serialize(g.Key) + ":" + Canonical(g.Single().Value) : throw new IOException("tailscale.duplicate_preference"))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Canonical)) + "]",
        _ => element.GetRawText()
    };

    internal static string[] Arguments(TailscalePrepareRequest request) => request.Action switch
    {
        TailscaleAction.Connect when request.Configuration is null => ["up"],
        TailscaleAction.Disconnect when request.Configuration is null => ["down"],
        TailscaleAction.Configure when request.Configuration is { } config =>
            ["set", "--accept-dns=" + (config.AcceptDns ? "true" : "false"), "--accept-routes=" + (config.AcceptRoutes ? "true" : "false")],
        TailscaleAction.ConfigurePreferences when request.Preferences is { } preferences =>
            PreferenceArguments(preferences),
        TailscaleAction.Logout when request.Configuration is null && request.Preferences is null => ["logout"],
        _ => throw new IOException("tailscale.invalid_request")
    };

    /// <summary>Every official preference is stated explicitly, so one edit cannot leave another stale.</summary>
    private static string[] PreferenceArguments(TailscalePreferences preferences)
    {
        if (!OperatingSystem.IsWindows() && preferences.Unattended)
            throw new IOException("tailscale.invalid_request");
        var arguments = new List<string>
        {
            "set",
            "--accept-dns=" + (preferences.AcceptDns ? "true" : "false"),
            "--accept-routes=" + (preferences.AcceptRoutes ? "true" : "false"),
            "--shields-up=" + (preferences.ShieldsUp ? "true" : "false"),
            "--exit-node=" + (preferences.ExitNode ?? ""),
            "--exit-node-allow-lan-access=" + (preferences.ExitNodeAllowLanAccess ? "true" : "false"),
            "--auto-update=" + (preferences.AutoUpdate ? "true" : "false"),
            "--advertise-exit-node=" + (preferences.AdvertiseExitNode ? "true" : "false"),
        };
        if (OperatingSystem.IsWindows()) arguments.Add("--unattended=" + (preferences.Unattended ? "true" : "false"));
        return [.. arguments];
    }

    public Task<bool> IsQuiescentAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Include every CLI instance, even one outside the fixed path. False positives fail closed.
        var processes = System.Diagnostics.Process.GetProcessesByName("tailscale");
        try { return Task.FromResult(processes.Length == 0); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public async Task<bool> ApplyAsync(TailscalePrepareRequest request, CancellationToken ct)
    {
        TailscaleControlProtection.RequireExecutable();
        if (request.Action == TailscaleAction.ConfigurePreferences && request.Preferences is { } preferences)
            await RequireExitNodeAsync(preferences, ct);
        return (await TailscaleStatusCommand.RunFixedAsync(Arguments(request), ct)).State == "observed";
    }

    /// <summary>The client may only choose a peer the daemon currently offers as an exit node.</summary>
    private static async Task RequireExitNodeAsync(TailscalePreferences preferences, CancellationToken ct)
    {
        var exitNode = preferences.ExitNode;
        if (string.IsNullOrEmpty(exitNode)) return;
        // Official automatic selection is resolved by the daemon against the tailnet's suggestions.
        if (exitNode == "auto:any") return;
        if (!IPAddress.TryParse(exitNode, out var requested))
            throw new IOException("tailscale.invalid_request");
        var status = await TailscaleStatusCommand.RunFixedAsync(["status", "--json"], ct);
        if (status.State != "observed") throw new IOException("tailscale.control_unavailable");
        using var document = JsonDocument.Parse(status.Json!);
        if (!document.RootElement.TryGetProperty("Peer", out var peers) || peers.ValueKind != JsonValueKind.Object)
            throw new IOException("tailscale.exit_node_unavailable");
        foreach (var peer in peers.EnumerateObject())
        {
            if (peer.Value.ValueKind != JsonValueKind.Object) continue;
            if (!peer.Value.TryGetProperty("ExitNodeOption", out var option) || option.ValueKind != JsonValueKind.True) continue;
            if (!peer.Value.TryGetProperty("TailscaleIPs", out var ips) || ips.ValueKind != JsonValueKind.Array) continue;
            foreach (var ip in ips.EnumerateArray())
                if (ip.ValueKind == JsonValueKind.String && IPAddress.TryParse(ip.GetString(), out var address)
                    && address.Equals(requested)) return;
        }
        throw new IOException("tailscale.exit_node_unavailable");
    }
}
