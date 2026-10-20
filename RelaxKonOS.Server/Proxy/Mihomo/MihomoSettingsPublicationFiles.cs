using System.Security.Cryptography;
using System.Globalization;
using RelaxKonOS.Protocol.ManagedRuntimes;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy.Platform;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Frozen application settings intent. Never a public DTO or a Helper authorization.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MihomoSettingsPublication([property: JsonRequired] string? PreviousIdentity,
    [property: JsonRequired] ProxySettingsDto Previous, [property: JsonRequired] ProxySettingsDto Candidate);

/// <summary>Reconciles the exact settings file and the ownership-aware host proxy after a measured runtime commit.
/// Every caller owns the publication gate; failures leave the encrypted intent pending for recovery.</summary>
public sealed class MihomoSettingsPublicationFiles(IProxyPlatformPaths paths, IHostSystemProxyService systemProxy)
{
    internal static readonly ProxySettingsDto Defaults = new(false, false, true, true, false, "warning", 7890,
        false, "127.0.0.1", ProxyTunSettingsDto.Default, ProxySystemProxyOptionsDto.Default);
    private string SettingsPath => Path.Combine(paths.GetStateDirectory(), "mihomo-settings.json");
    private const int MaximumBytes = 64 * 1024;
    internal static ProxySettingsDto Normalize(ProxySettingsDto settings) => settings with
    { Tun = settings.Tun ?? ProxyTunSettingsDto.Default, SystemProxy = settings.SystemProxy ?? ProxySystemProxyOptionsDto.Default };

    internal async Task<MihomoSettingsPublication> CaptureAsync(ProxySettingsDto previous, ProxySettingsDto candidate, CancellationToken ct)
    {
        var bytes = await ReadBytesAsync(ct);
        if (bytes is not null)
        {
            try
            {
                var current = JsonSerializer.Deserialize<ProxySettingsDto>(bytes);
                if (current is null || Normalize(current) != Normalize(previous)) Fail();
            }
            catch (JsonException) { Fail(); }
        }
        else if (Normalize(previous) != Defaults) Fail();
        var intent = new MihomoSettingsPublication(bytes is null ? null : Identity(bytes), Normalize(previous), Normalize(candidate));
        Validate(intent);
        return intent;
    }

    internal async Task<MihomoSettingsPublication> CaptureRemovalAsync(CancellationToken ct)
    {
        var bytes = await ReadBytesAsync(ct);
        var previous = Defaults;
        if (bytes is not null)
        {
            try { previous = Normalize(JsonSerializer.Deserialize<ProxySettingsDto>(bytes) ?? throw new JsonException()); }
            catch (JsonException) { Fail(); }
        }
        var intent = await CaptureAsync(previous, previous with { SystemProxyEnabled = false }, ct);
        ValidateRemoval(intent);
        return intent;
    }

    internal static void ValidateRemoval(MihomoSettingsPublication settings)
    {
        if (settings is null) throw new RuntimePolicyException("runtime.removal_settings_invalid");
        Validate(settings);
        if (settings.Candidate != (settings.Previous with { SystemProxyEnabled = false }))
            throw new RuntimePolicyException("runtime.removal_settings_invalid");
    }

    internal Task CommitRemovalAsync(MihomoSettingsPublication intent, CancellationToken ct)
    {
        ValidateRemoval(intent);
        return CommitCoreAsync(intent, requireHostProof: true, ct);
    }

    internal static MihomoPublishedRuntime Candidate(
        MihomoPublishedRuntime previous, ProxySettingsDto settings)
    {
        var activation = MihomoManagedConfiguration.ReadTunActivation(previous.Yaml);
        var yaml = MihomoManagedConfiguration.WithServerControllerSettings(
            MihomoManagedConfiguration.WithServerGeoDataSettings(
                MihomoManagedConfiguration.WithRuntimeSettings(
                    MihomoManagedConfiguration.WithManagedTunSettings(previous.Yaml, settings,
                        activation.Enabled, activation.RouteExclusions), settings)),
            new MihomoControllerOptions { Endpoint = new("http://127.0.0.1:" + previous.ControllerPort.ToString(CultureInfo.InvariantCulture) + "/") }, previous.ControllerSecret);
        return MihomoConfigurationPolicy.Describe(yaml).Material;
    }

    internal static void Validate(MihomoSettingsPublication settings)
    {
        if (settings.Previous is null || settings.Candidate is null
            || settings.PreviousIdentity is { } identity && (identity.Length != 64 || !identity.All(Uri.IsHexDigit))) Fail();
        foreach (var value in new[] { settings.Previous, settings.Candidate })
            if (value.MixedPort is < 1 or > 65535 || value.SystemProxyHost is not { Length: > 0 and <= 128 }
                || value.LogLevel is not ("silent" or "error" or "warning" or "info" or "debug")
                || value.Tun is null || value.SystemProxy is null || JsonSerializer.SerializeToUtf8Bytes(value).Length > MaximumBytes) Fail();
    }

    internal async Task VerifyAsync(MihomoSettingsPublication intent, bool allowCandidate, CancellationToken ct)
    {
        Validate(intent);
        var current = await ReadBytesAsync(ct);
        var identity = current is null ? null : Identity(current);
        if (identity != intent.PreviousIdentity && (!allowCandidate || identity != Identity(JsonSerializer.SerializeToUtf8Bytes(intent.Candidate)))) Fail();
    }

    internal Task CommitAsync(MihomoSettingsPublication intent, CancellationToken ct) => CommitCoreAsync(intent, requireHostProof: false, ct);

    private async Task CommitCoreAsync(MihomoSettingsPublication intent, bool requireHostProof, CancellationToken ct)
    {
        await VerifyAsync(intent, allowCandidate: true, ct);
        // The host adapter preserves unrelated/external edits and journals its own original values.
        var problem = await systemProxy.ApplyAsync(intent.Candidate, intent.Previous, enforce: false, ct);
        if (requireHostProof && problem == ProxyProblemCodes.PrivilegedOperationUnavailable)
            throw new RuntimePolicyException("runtime.helper_unavailable");
        if (problem is not null && !(!requireHostProof && problem == ProxyProblemCodes.PrivilegedOperationUnavailable
            && !intent.Candidate.SystemProxyEnabled && !intent.Previous.SystemProxyEnabled))
            throw new RuntimePolicyException("runtime.settings_host_apply_failed");
        // Check again after host IPC: never overwrite an unrelated settings edit in the crash window.
        await VerifyAsync(intent, allowCandidate: true, ct);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(intent.Candidate);
        var existing = await ReadBytesAsync(ct);
        if (existing is not null && Identity(existing) == Identity(bytes)) return;
        var directory = Path.GetDirectoryName(SettingsPath)!;
        RuntimeSourceStore.RequireNoLinks(directory);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".settings");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(temporary, options)) { await output.WriteAsync(bytes, ct); output.Flush(true); }
            RuntimeSourceStore.RequireNoLinks(SettingsPath);
            File.Move(temporary, SettingsPath, true);
        }
        finally { File.Delete(temporary); }
    }

    private async Task<byte[]?> ReadBytesAsync(CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(SettingsPath);
        if (Directory.Exists(SettingsPath)) Fail();
        if (!File.Exists(SettingsPath)) return null;
        await using var input = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length is 0 or > MaximumBytes) Fail();
        var bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes, ct);
        return bytes;
    }
    private static string Identity(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    [DoesNotReturn]
    private static void Fail() => throw new RuntimePolicyException("runtime.settings_drift");
}
