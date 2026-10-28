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
            var id = root.GetProperty("Self").GetProperty("ID").GetString();
            var version = root.GetProperty("Version").GetString();
            if (root.TryGetProperty("AuthURL", out var auth) && auth.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(auth.GetString())
                || root.GetProperty("Self").TryGetProperty("Expired", out var expired) && expired.ValueKind == JsonValueKind.True)
                throw new IOException("tailscale.login_required");
            if (state is not ("Running" or "Stopped") || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
                throw new IOException("tailscale.control_unavailable");
            return JsonSerializer.Serialize(new { state, id, version });
        }
        var identity = Identity(before.RootElement);
        if (identity != Identity(after.RootElement)) throw new IOException("tailscale.observation_changed");
        var settings = prefs.RootElement;
        if (settings.ValueKind != JsonValueKind.Object) throw new IOException("tailscale.invalid_preferences");
        // Reject duplicate properties and normalize ordering for a stable, complete preference revision.
        var canonical = Canonical(settings);
        var revision = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity + "\n" + canonical)));
        var dns = settings.GetProperty("accept-dns").GetBoolean();
        var routes = settings.GetProperty("accept-routes").GetBoolean();
        return new(revision, before.RootElement.GetProperty("BackendState").GetString() == "Running", true, new(dns, routes), true);
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
        _ => throw new IOException("tailscale.invalid_request")
    };

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
        return (await TailscaleStatusCommand.RunFixedAsync(Arguments(request), ct)).State == "observed";
    }
}
