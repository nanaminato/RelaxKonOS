using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.ProxyServer;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Native version/build/configuration checks for the program a managed installation actually recorded.
/// Fixed host implementation; never supplied by a client.
/// </summary>
internal interface ISingBoxNativeChecks
{
    /// <summary>Returns the identity of the checked configuration bytes.</summary>
    Task<string> ValidateAsync(string binary, string configuration, RuntimeRelease release, byte[] bytes, CancellationToken ct);
}

internal sealed class SingBoxNativeChecks : ISingBoxNativeChecks
{
    public async Task<string> ValidateAsync(string binary, string configuration, RuntimeRelease release, byte[] bytes,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var build = SingBoxNativeValidation.Parse(await SingBoxOperations.RunAsync(binary, ["version"]), release.OriginalVersion,
            release.Target.Os, release.Target.Architecture);
        SingBoxNativeValidation.RequireConfigurationFeatures(build, bytes);
        await SingBoxOperations.RunAsync(binary, ["check", "-c", configuration]);
        ct.ThrowIfCancellationRequested();
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

/// <summary>
/// Published-configuration and service control for the one archive-managed installation. The caller holds
/// the shared instance lock. This never downloads a program, changes the active revision, or creates trust;
/// runtime versions stay behind the lifecycle transaction.
/// </summary>
internal static class SingBoxManagedRuntime
{
    private const string Drift = "proxy_server.configuration_drift";
    private const string Recovery = "proxy_server.recovery_required";
    private const string Missing = "proxy_server.configuration_missing";
    private const string ApplyFailed = "proxy_server.config_apply_failed";
    private const string RuntimeFailed = "proxy_server.runtime_failed";

    private static string Configuration(string root) => Path.Combine(root, "config.json");
    private static string Previous(string root) => Path.Combine(root, "config.json.previous");
    private static string Candidate(string root) => Path.Combine(root, "config.json.candidate");

    /// <summary>Stop stays with <see cref="SingBoxManagedStop"/> so it remains available during recovery.</summary>
    internal static async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, string root,
        RuntimeArchiveStateStore.Session session, ISingBoxArchiveHost host, CancellationToken ct, ISingBoxNativeChecks? natives = null)
    {
        var state = session.State ?? throw new RuntimePolicyException("runtime.state_unknown");
        natives ??= new SingBoxNativeChecks();
        switch (request.Operation)
        {
            case PrivilegedOperationKind.ProxyServerStatus:
                return Json(await StatusAsync(root, state, host, ct));
            // Recovering a runtime switch needs the lifecycle's fresh authorization; never clear it here.
            case PrivilegedOperationKind.ProxyServerAction when request.ProxyServer is { Action: ProxyServerAction.Recover }:
                return Json(await StatusAsync(root, state, host, ct));
            case PrivilegedOperationKind.ProxyServerGenerateKeys:
            case PrivilegedOperationKind.ProxyServerValidate:
            case PrivilegedOperationKind.ProxyServerApply:
            case PrivilegedOperationKind.ProxyServerAction:
                break;
            default: return Failure("proxy_server.request_invalid");
        }
        if (state.Active is null || state.Pending is not null || state.Removal is not null || state.Configuration is not null
            || state.Installation.RecoveryRequired) return Failure(Recovery);
        return request.Operation switch
        {
            PrivilegedOperationKind.ProxyServerGenerateKeys => await GenerateKeysAsync(root, state),
            PrivilegedOperationKind.ProxyServerValidate => await PublishAsync(request, root, state, session, host, validateOnly: true, natives, ct),
            PrivilegedOperationKind.ProxyServerApply => await PublishAsync(request, root, state, session, host, validateOnly: false, natives, ct),
            _ => await ActionAsync(request, root, state, session, host, natives, ct)
        };
    }

    private static async Task<PrivilegedOperationResult> ActionAsync(PrivilegedOperationRequest request, string root,
        RuntimeArchiveState state, RuntimeArchiveStateStore.Session session, ISingBoxArchiveHost host,
        ISingBoxNativeChecks natives, CancellationToken ct)
    {
        if (request.ProxyServer is not { Action: { } action, Material: null }) return Failure("proxy_server.request_invalid");
        switch (action)
        {
            // The program is owned by the runtime lifecycle; an existing managed installation has nothing to fetch here.
            case ProxyServerAction.Install or ProxyServerAction.Upgrade: return Json(await StatusAsync(root, state, host, ct));
            case ProxyServerAction.Uninstall: return Failure("proxy_server.managed_runtime_required");
            case ProxyServerAction.Enable or ProxyServerAction.Disable:
                return await SetBootAsync(root, state, host, action == ProxyServerAction.Enable, ct);
            case ProxyServerAction.Start: return await StartAsync(root, state, host, restart: false, natives, ct);
            case ProxyServerAction.Restart: return await StartAsync(root, state, host, restart: true, natives, ct);
            case ProxyServerAction.Rollback: return await RollbackConfigurationAsync(root, state, session, host, natives, ct);
            default: return Failure("proxy_server.request_invalid");
        }
    }

    /// <summary>The Server's own published identity, never a draft or a newly rendered candidate.</summary>
    private static async Task<ProxyServerStatus> StatusAsync(string root, RuntimeArchiveState state, ISingBoxArchiveHost host,
        CancellationToken ct)
    {
        var active = state.Active;
        var pending = state.Pending is not null || state.Removal is not null || state.Configuration is not null || state.Installation.RecoveryRequired;
        SingBoxServiceObservation observed;
        try { observed = await host.ObserveAsync(ct); }
        catch (RuntimePolicyException)
        {
            // An unreadable or foreign service registration never proves a missing installation.
            return new(true, active is not null, false, pending, active?.MeasuredVersion, state.ConfigurationIdentity,
                false, pending ? Recovery : RuntimeFailed, false, null);
        }
        var running = observed.Exists && observed.Owned && observed.Running;
        var ours = !observed.Exists || active is not null && observed.Owned && SamePath(observed.Executable, Binary(root, active));
        var recorded = state.ConfigurationIdentity;
        var hash = active is null ? null : await DigestAsync(Configuration(root), ct);
        var drift = active is not null && recorded is not null && (hash is null
            || !hash.Equals(recorded, StringComparison.OrdinalIgnoreCase)
            || running && !recorded.Equals(observed.AppliedConfigurationIdentity, StringComparison.OrdinalIgnoreCase));
        var applied = running && ours && !drift && !pending && recorded is not null && hash is not null
            && hash.Equals(recorded, StringComparison.OrdinalIgnoreCase);
        return new(true, active is not null, running, pending, active?.MeasuredVersion, recorded ?? hash, applied,
            pending ? Recovery : drift ? Drift : ours ? null : RuntimeFailed, StatisticsSupported: false,
            BootEnabled: observed.Exists && observed.Owned ? observed.BootEnabled : null);
    }

    private static async Task<PrivilegedOperationResult> GenerateKeysAsync(string root, RuntimeArchiveState state)
    {
        var output = await SingBoxOperations.RunAsync(Binary(root, state.Active!), ["generate", "reality-keypair"]);
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var privateKey = lines.Single(l => l.StartsWith("PrivateKey: ", StringComparison.Ordinal))[12..];
        var publicKey = lines.Single(l => l.StartsWith("PublicKey: ", StringComparison.Ordinal))[11..];
        if (!SingBoxConfiguration.Key(privateKey) || !SingBoxConfiguration.Key(publicKey)) return Failure("proxy_server.keys_invalid");
        return Json(new ProxyServerRealityKey(Guid.Empty, privateKey, publicKey,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()));
    }

    private static async Task<PrivilegedOperationResult> PublishAsync(PrivilegedOperationRequest request, string root,
        RuntimeArchiveState state, RuntimeArchiveStateStore.Session session, ISingBoxArchiveHost host, bool validateOnly,
        ISingBoxNativeChecks natives, CancellationToken ct)
    {
        if (request.ProxyServer is not { Action: null, Material: { } material }) return Failure("proxy_server.request_invalid");
        var active = state.Active!;
        var binary = Binary(root, active);
        var observed = await host.ObserveAsync(ct);
        // The registered program must be the immutable revision this installation recorded.
        if (observed.Exists && (!observed.Owned || observed.BootEnabled is null || !SamePath(observed.Executable, binary)))
            return Failure(RuntimeFailed);
        if (!await VerifyFilesAsync(root, active, ct)) return Failure(RuntimeFailed);
        var configuration = Configuration(root);
        var current = await DigestAsync(configuration, ct);
        if (!string.Equals(current, state.ConfigurationIdentity, StringComparison.OrdinalIgnoreCase)) return Failure(Drift);
        var rendered = Encoding.UTF8.GetBytes(SingBoxConfiguration.Render(material));
        foreach (var inbound in material.Configuration.Inbounds.Where(i => i.Enabled && i.Protocol == ProxyServerProtocol.VlessReality))
        {
            var addresses = await Dns.GetHostAddressesAsync(inbound.HandshakeHost, ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (addresses.Length == 0 || addresses.Any(a => !SingBoxConfiguration.PublicAddress(a)))
                return Failure("proxy_server.handshake_address_forbidden");
        }
        var candidate = Candidate(root);
        try
        {
            await WriteNewAsync(candidate, rendered, ct);
            var identity = await natives.ValidateAsync(binary, candidate, active.Release, rendered, ct);
            if (validateOnly) return Json(new ProxyServerValidation(true, []));
            var wasRunning = observed.Exists && observed.Running;
            var bootEnabled = observed.Exists && observed.BootEnabled == true;
            await BackupAsync(root, ct);
            try
            {
                if (wasRunning) await host.StopAsync(ct);
                await ReplaceAsync(configuration, candidate, ct, keepSource: false);
                await host.ConfigureAsync(binary, configuration, identity, bootEnabled, ct);
                if (wasRunning) await host.StartAsync(ct);
                var after = await host.ObserveAsync(ct);
                if (!after.Exists || !after.Owned || !SamePath(after.Executable, binary)
                    || after.Running && (!string.Equals(after.AppliedConfigurationIdentity, identity, StringComparison.OrdinalIgnoreCase)
                        || !await host.ListenersHealthyAsync(material.Configuration, ct)))
                    throw new RuntimePolicyException(ApplyFailed);
            }
            catch (Exception)
            {
                if (!await CompensateAsync(root, state, host, wasRunning, bootEnabled, ct)) return Failure(Recovery);
                return Failure(ApplyFailed);
            }
            var updated = Commit(session, state, identity);
            return Json(await StatusAsync(root, updated, host, ct));
        }
        finally { File.Delete(candidate); }
    }

    /// <summary>Restores the configuration this publication replaced; the Server owns the revision it maps to.</summary>
    private static async Task<PrivilegedOperationResult> RollbackConfigurationAsync(string root, RuntimeArchiveState state,
        RuntimeArchiveStateStore.Session session, ISingBoxArchiveHost host, ISingBoxNativeChecks natives, CancellationToken ct)
    {
        var previous = Previous(root);
        if (!File.Exists(previous)) return Failure(Missing);
        var active = state.Active!;
        var binary = Binary(root, active);
        var observed = await host.ObserveAsync(ct);
        if (observed.Exists && (!observed.Owned || observed.BootEnabled is null || !SamePath(observed.Executable, binary)))
            return Failure(RuntimeFailed);
        if (!await VerifyFilesAsync(root, active, ct)) return Failure(RuntimeFailed);
        var configuration = Configuration(root);
        var bytes = await ReadAsync(previous, ct);
        var candidate = Candidate(root);
        var wasRunning = observed.Exists && observed.Running;
        var bootEnabled = observed.Exists && observed.BootEnabled == true;
        try
        {
            await WriteNewAsync(candidate, bytes, ct);
            var identity = await natives.ValidateAsync(binary, candidate, active.Release, bytes, ct);
            if (wasRunning) await host.StopAsync(ct);
            await ReplaceAsync(configuration, candidate, ct, keepSource: false);
            await host.ConfigureAsync(binary, configuration, identity, bootEnabled, ct);
            if (wasRunning) await host.StartAsync(ct);
            var after = await host.ObserveAsync(ct);
            if (!after.Exists || !after.Owned || !SamePath(after.Executable, binary)
                || after.Running && !string.Equals(after.AppliedConfigurationIdentity, identity, StringComparison.OrdinalIgnoreCase))
                return Failure(ApplyFailed);
            var updated = Commit(session, state, identity);
            return Json(await StatusAsync(root, updated, host, ct));
        }
        finally { File.Delete(candidate); }
    }

    private static async Task<PrivilegedOperationResult> StartAsync(string root, RuntimeArchiveState state, ISingBoxArchiveHost host,
        bool restart, ISingBoxNativeChecks natives, CancellationToken ct)
    {
        var active = state.Active!;
        var binary = Binary(root, active);
        var configuration = Configuration(root);
        if (!File.Exists(configuration)) return Failure(Missing);
        if (!await VerifyFilesAsync(root, active, ct)) return Failure(RuntimeFailed);
        var identity = await DigestAsync(configuration, ct);
        if (identity is null || !identity.Equals(state.ConfigurationIdentity, StringComparison.OrdinalIgnoreCase)) return Failure(Drift);
        var observed = await host.ObserveAsync(ct);
        if (observed.Exists && (!observed.Owned || observed.BootEnabled is null || !SamePath(observed.Executable, binary)))
            return Failure(RuntimeFailed);
        await natives.ValidateAsync(binary, configuration, active.Release, await ReadAsync(configuration, ct), ct);
        if (restart && observed.Running) await host.StopAsync(ct);
        // A published configuration that was never registered is applied under that exact identity first.
        if (!observed.Exists) await host.ConfigureAsync(binary, configuration, identity, false, ct);
        if (!observed.Running || restart) await host.StartAsync(ct);
        var after = await host.ObserveAsync(ct);
        if (!after.Running || !string.Equals(after.AppliedConfigurationIdentity, identity, StringComparison.OrdinalIgnoreCase))
            return Failure(ApplyFailed);
        return Json(await StatusAsync(root, state, host, ct));
    }

    private static async Task<PrivilegedOperationResult> SetBootAsync(string root, RuntimeArchiveState state, ISingBoxArchiveHost host,
        bool enabled, CancellationToken ct)
    {
        var observed = await host.ObserveAsync(ct);
        if (!observed.Exists || !observed.Owned || !SamePath(observed.Executable, Binary(root, state.Active!))) return Failure(Missing);
        await host.SetBootAsync(enabled, ct);
        var after = await host.ObserveAsync(ct);
        return after.BootEnabled == enabled ? Json(await StatusAsync(root, state, host, ct)) : Failure(RuntimeFailed);
    }

    /// <summary>Restores the replaced file, registration and run state; only a verified state returns true.</summary>
    private static async Task<bool> CompensateAsync(string root, RuntimeArchiveState state, ISingBoxArchiveHost host,
        bool wasRunning, bool bootEnabled, CancellationToken ct)
    {
        var recorded = state.ConfigurationIdentity!;
        var configuration = Configuration(root);
        try
        {
            var bytes = await ReadAsync(Previous(root), ct);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(recorded, StringComparison.OrdinalIgnoreCase)) return false;
            var candidate = Candidate(root);
            try
            {
                await WriteNewAsync(candidate, bytes, ct);
                await host.StopAsync(ct);
                await ReplaceAsync(configuration, candidate, ct, keepSource: false);
                await host.ConfigureAsync(Binary(root, state.Active!), configuration, recorded, bootEnabled, ct);
                if (wasRunning) await host.StartAsync(ct);
            }
            finally { File.Delete(candidate); }
            var after = await host.ObserveAsync(ct);
            return after.Owned && after.Running == wasRunning
                && (!wasRunning || recorded.Equals(after.AppliedConfigurationIdentity, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or RuntimePolicyException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private static RuntimeArchiveState Commit(RuntimeArchiveStateStore.Session session, RuntimeArchiveState state, string identity)
    {
        var updated = state with
        {
            ConfigurationIdentity = identity,
            Installation = state.Installation with { StateRevision = checked(state.Installation.StateRevision + 1) }
        };
        session.Save(updated);
        return updated;
    }

    /// <summary>Executed binaries must still be the immutable files the installation recorded.</summary>
    private static async Task<bool> VerifyFilesAsync(string root, RuntimeStoredRevision revision, CancellationToken ct)
    {
        var directory = Path.Combine(root, "versions", revision.RevisionId.ToString("N"));
        foreach (var file in revision.Release.Artifact.Layout.Files)
        {
            var path = Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || new FileInfo(path).Length != file.Length) return false;
            var digest = await DigestAsync(path, ct);
            if (digest is null || !digest.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static string Binary(string root, RuntimeStoredRevision revision) => Path.Combine(root, "versions",
        revision.RevisionId.ToString("N"), revision.Release.Artifact.EntryPoint.Replace('/', Path.DirectorySeparatorChar));

    private static bool SamePath(string? left, string right) => left is not null && string.Equals(Path.GetFullPath(left),
        Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static async Task<string?> DigestAsync(string path, CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        if (!File.Exists(path)) return null;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
    }

    private static async Task<byte[]> ReadAsync(string path, CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 4 * 1024 * 1024) throw new RuntimePolicyException("runtime.native_configuration_invalid");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, ct);
        return bytes;
    }

    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var output = new FileStream(path, options);
        await output.WriteAsync(bytes, ct);
        output.Flush(true);
    }

    /// <summary>The replaced file is kept as <c>config.json.previous</c> for the Server's own rollback.</summary>
    private static async Task BackupAsync(string root, CancellationToken ct)
    {
        var configuration = Configuration(root);
        var previous = Previous(root);
        if (!File.Exists(configuration)) return;
        var temporary = previous + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            await WriteNewAsync(temporary, await ReadAsync(configuration, ct), ct);
            RuntimeSourceStore.RequireNoLinks(previous);
            File.Move(temporary, previous, true);
        }
        finally { File.Delete(temporary); }
    }

    private static async Task ReplaceAsync(string destination, string source, CancellationToken ct, bool keepSource)
    {
        var bytes = await ReadAsync(source, ct);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            await WriteNewAsync(temporary, bytes, ct);
            RuntimeSourceStore.RequireNoLinks(destination);
            File.Move(temporary, destination, true);
        }
        finally { File.Delete(temporary); }
        if (!keepSource) File.Delete(source);
    }

    private static PrivilegedOperationResult Json<T>(T value) => new(true, OutputBase64: Convert.ToBase64String(
        JsonSerializer.SerializeToUtf8Bytes(value, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default)));
    private static PrivilegedOperationResult Failure(string code) => new(false, ExitCode: 1,
        ProblemCode: PrivilegedProblemCode.InvalidRequest, Error: code);
}
