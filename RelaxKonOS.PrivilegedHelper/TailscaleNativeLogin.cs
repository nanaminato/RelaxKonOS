using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Server.Tailscale;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>A transient secret container. Native login links are never serialized or printed implicitly.</summary>
internal sealed class TailscaleLoginNativeResult(string state, string? authenticationUrl = null, TailscaleLoginBaseline? observation = null) : IDisposable
{
    private string? _authenticationUrl = authenticationUrl;
    public string State { get; } = state;
    internal TailscaleLoginBaseline? Observation { get; } = observation;
    internal string? TakeAuthenticationUrl() => Interlocked.Exchange(ref _authenticationUrl, null);
    public override string ToString() => "TailscaleLoginNativeResult { State = " + State + " }";
    public void Dispose() => Interlocked.Exchange(ref _authenticationUrl, null);
}

internal static class TailscaleLoginOutput
{
    // Exact ASCII origin/path, with an opaque token. No URI normalization expands this allowlist.
    internal static bool IsOfficialUrl(string? value)
    {
        const string prefix = "https://login.tailscale.com/a/";
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var token = value.AsSpan(prefix.Length);
        if (token.Length is < 10 or > 256) return false;
        foreach (var character in token)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')) return false;
        return true;
    }

    internal static TailscaleLoginNativeResult Parse(ReadOnlySpan<byte> bytes, int exitCode)
    {
        if (bytes.Length is 0 or > 1024 * 1024) throw Invalid();
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowMultipleValues = true, MaxDepth = 16 });
            var count = 0; string? url = null; string? state = null; var terminal = false;
            while (reader.Read())
            {
                if (++count > 4 || terminal || reader.TokenType != JsonTokenType.StartObject) throw Invalid();
                using var document = JsonDocument.ParseValue(ref reader);
                var root = document.RootElement;
                foreach (var property in root.EnumerateObject().GroupBy(x => x.Name, StringComparer.Ordinal))
                    if (property.Count() != 1 || property.Key is not ("AuthURL" or "QR" or "BackendState" or "Error")) throw Invalid();
                if (!root.TryGetProperty("BackendState", out var backend) || backend.ValueKind != JsonValueKind.String) throw Invalid();
                state = backend.GetString();
                if (state is not ("NeedsLogin" or "NeedsMachineAuth" or "Running")) throw Invalid();
                if (root.TryGetProperty("Error", out var error)
                    && (error.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(error.GetString()))) throw Invalid();
                if (root.TryGetProperty("QR", out var qr) && qr.ValueKind != JsonValueKind.String) throw Invalid();
                if (root.TryGetProperty("AuthURL", out var auth))
                {
                    if (state != "NeedsLogin" || auth.ValueKind != JsonValueKind.String || !IsOfficialUrl(auth.GetString())) throw Invalid();
                    var candidate = auth.GetString()!;
                    if (url is not null && url != candidate) throw Invalid();
                    url = candidate;
                }
                if (state == "Running") terminal = true;
            }
            if (count == 0) throw Invalid();
            if (terminal) return exitCode == 0 ? new("completed") : throw Invalid();
            if (state == "NeedsMachineAuth") return new("approval-required");
            return url is not null ? new("awaiting-browser", url) : throw Invalid();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        { throw Invalid(); }
    }
    private static InvalidDataException Invalid() => new("tailscale.login.invalid_output");
}

/// <summary>No URL is part of the baseline, durable operation metadata or diagnostic representation.</summary>
internal sealed record TailscaleLoginBaseline(string Revision, string PreferencesRevision, string BackendState, bool CanStart);

/// <summary>Fixed native first-login adapter. It is intentionally not registered as a public operation yet.</summary>
internal sealed class TailscaleNativeLogin
{
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<TailscaleCapturedResult>> _run;
    private readonly Action _requireExecutable;
    internal TailscaleNativeLogin() : this(TailscaleStatusCommand.CaptureFixedAsync, TailscaleControlProtection.RequireExecutable) { }
    internal TailscaleNativeLogin(Func<IReadOnlyList<string>, CancellationToken, Task<TailscaleCapturedResult>> run, Action requireExecutable)
    { _run = run; _requireExecutable = requireExecutable; }

    internal async Task<TailscaleLoginBaseline> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _requireExecutable();
        var first = await Read("status", ct);
        var preferences = await Read("get", ct);
        var last = await Read("status", ct);
        return ParseBaseline(first, preferences, last);
    }
    private async Task<string> Read(string verb, CancellationToken ct)
    {
        var result = await _run([verb, "--json"], ct);
        if (result.State != "exited" || result.ExitCode != 0 || result.Output is null) throw Invalid();
        return result.Output;
    }
    internal static TailscaleLoginBaseline ParseBaseline(string first, string preferences, string last)
    {
        try
        {
            using var before = JsonDocument.Parse(first);
            using var after = JsonDocument.Parse(last);
            using var prefs = JsonDocument.Parse(preferences);
            static (string Fingerprint, string State, bool Ready) Identity(JsonElement root)
            {
                // Validate duplicate fields before selecting the small identity projection.
                TailscaleNativeControl.Canonical(root);
                var state = root.GetProperty("BackendState").GetString();
                var version = root.GetProperty("Version").GetString();
                if (state is not ("NeedsLogin" or "NeedsMachineAuth" or "Running" or "Stopped") || string.IsNullOrWhiteSpace(version)) throw Invalid();
                var id = root.TryGetProperty("Self", out var self) && self.ValueKind == JsonValueKind.Object
                    && self.TryGetProperty("ID", out var identity) && identity.ValueKind == JsonValueKind.String ? identity.GetString() : "";
                var challenge = root.TryGetProperty("AuthURL", out var auth) ? auth.GetString() : "";
                var fingerprint = JsonSerializer.Serialize(new { state, version, id, challenge = Hash(challenge ?? "") });
                return (fingerprint, state, state == "NeedsLogin" && string.IsNullOrEmpty(challenge));
            }
            var initial = Identity(before.RootElement); var final = Identity(after.RootElement);
            if (initial != final || prefs.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            // Confirm the declared preference shape; no defaults or native-flag reconstruction.
            prefs.RootElement.GetProperty("accept-dns").GetBoolean(); prefs.RootElement.GetProperty("accept-routes").GetBoolean();
            var prefsHash = Hash(TailscaleNativeControl.Canonical(prefs.RootElement));
            return new(Hash(initial.Fingerprint + "\n" + prefsHash), prefsHash, initial.State, initial.Ready);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or IOException)
        { throw Invalid(); }
    }
    internal async Task<TailscaleLoginNativeResult> BeginAsync(string expectedRevision, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        ct = deadline.Token;
        if (expectedRevision is not { Length: 64 } || !expectedRevision.All(Uri.IsHexDigit)) throw new InvalidDataException("tailscale.login.state_changed");
        var baseline = await ReadAsync(ct);
        if (!baseline.CanStart || baseline.Revision != expectedRevision) throw new InvalidDataException("tailscale.login.state_changed");
        _requireExecutable();
        var captured = await _run(["up", "--json", "--timeout=5s"], ct);
        if (captured.State != "exited" || captured.ExitCode is null || captured.Output is null) throw Invalid();
        var result = TailscaleLoginOutput.Parse(Encoding.UTF8.GetBytes(captured.Output), captured.ExitCode.Value);
        try
        {
            var observed = await ReadAsync(ct);
            if (observed.PreferencesRevision != baseline.PreferencesRevision) throw Invalid();
            if (result.State == "completed" && observed.BackendState != "Running" || result.State == "approval-required" && observed.BackendState != "NeedsMachineAuth") throw Invalid();
            if (result.State == "awaiting-browser")
            {
                // Re-read the daemon's current URL and bind it to this command's challenge, then return it once.
                var secret = result.TakeAuthenticationUrl();
                var status = await Read("status", ct);
                using var json = JsonDocument.Parse(status);
                TailscaleNativeControl.Canonical(json.RootElement);
                if (json.RootElement.GetProperty("BackendState").GetString() != "NeedsLogin"
                    || json.RootElement.GetProperty("AuthURL").GetString() != secret || !TailscaleLoginOutput.IsOfficialUrl(secret)) throw Invalid();
                result.Dispose();
                return new("awaiting-browser", secret, observed);
            }
            var state = result.State; result.Dispose();
            return new(state, observation: observed);
        }
        catch (OperationCanceledException) { result.Dispose(); throw; }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or InvalidOperationException
            or KeyNotFoundException or UnauthorizedAccessException or ArgumentException)
        { result.Dispose(); throw Invalid(); }
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static InvalidDataException Invalid() => new("tailscale.login.invalid_output");
}
