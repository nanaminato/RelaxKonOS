using System.Diagnostics;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tailscale;

namespace RelaxKonOS.PrivilegedHelper;

// MSI is executed by Windows Installer, never extracted or represented as an archive switch.
internal static class TailscaleInstallerOperations
{
    private sealed record Commit(Guid OperationId, ComponentExecuteRequest Request, ComponentDownloadRelease Release,
        string ProductCode, string CliHash, string ServiceHash, bool Removed, string? NodeId, bool Disowned = false);
    private sealed record Pending(Guid OperationId, ComponentExecuteRequest Request, Commit? Previous, string ProductCode);
    private sealed record Maintenance(Guid OperationId, ComponentRecoveryRequest Request, DateTimeOffset RecordedAt);
    private static string Root => Path.Combine(TailscaleControlProtection.Root, "installation");
    private static string Cli => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
    private static string Daemon => Path.Combine(Path.GetDirectoryName(Cli)!, "tailscaled.exe");
    private static string Rid => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "win-arm64" : "win-x64";
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };

    internal static async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request)
    {
        if (!OperatingSystem.IsWindows()) return new(false, 64, ProblemCode: PrivilegedProblemCode.UnsupportedOperation);
        var clean = new PrivilegedOperationRequest(request.Operation, OperationId: request.OperationId, Correlation: request.Correlation,
            Version: request.Version, TailscaleInstaller: request.Operation == PrivilegedOperationKind.TailscaleInstallerExecute ? request.TailscaleInstaller : null,
            PackageRecovery: request.Operation == PrivilegedOperationKind.TailscaleInstallerRecover ? request.PackageRecovery : null,
            DownloadProxy: request.Operation == PrivilegedOperationKind.TailscaleInstallerExecute ? request.DownloadProxy : null);
        if (clean != request || request.OperationId is not { } id || id == Guid.Empty || request.Version != PrivilegedOperationProtocol.Version
            || request.Correlation?.IsValid() != true || request.Correlation.OperationId != id)
            return new(false, 64, ProblemCode: PrivilegedProblemCode.InvalidRequest);
        try
        {
            TailscaleControlProtection.PrepareRoot(TailscaleControlProtection.Root);
            TailscaleControlProtection.PrepareRoot(Root);
            using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(12));
            using var gate = await AcquireAsync(lifetime.Token);
            var current = Read<Commit>("current.json"); var pending = Read<Pending>("pending.json");
            if (current is not null && pending is not null && current.OperationId == pending.OperationId && current.Request == pending.Request
                && (await ObserveAsync(current, pending, current.OperationId, lifetime.Token)).VerifiedOperationId == current.OperationId)
            { Delete("pending.json"); pending = null; }
            if (request.Operation == PrivilegedOperationKind.TailscaleInstallerQuery)
            {
                var snapshot = await ObserveAsync(current, pending, id, lifetime.Token);
                if (Read<Maintenance>("maintenance-" + id.ToString("N") + ".json") is { OperationId: var maintained } && maintained == id)
                    snapshot = snapshot with { VerifiedOperationId = id };
                return new(true, TailscaleInstallerSnapshot: snapshot);
            }
            if (request.Operation == PrivilegedOperationKind.TailscaleInstallerRecover)
                return await RecoverAsync(request, current, pending, lifetime.Token);
            if (request.Operation != PrivilegedOperationKind.TailscaleInstallerExecute || request.TailscaleInstaller is not { } frozen)
                return new(false, 64, ProblemCode: PrivilegedProblemCode.InvalidRequest);
            var before = await ObserveAsync(current, pending, id, lifetime.Token);
            if (Read<Commit>(ReceiptName(id)) is { } original)
                return original.Request == frozen && before.VerifiedOperationId == id
                    ? new(true, TailscaleInstallerSnapshot: before) : Conflict(before);
            if (pending is not null || before.View.State is not { } state || state.RecoveryRequired
                || TailscaleControlOperations.HasPendingOperations()
                || !Valid(frozen, state) || frozen.Request.Change == RuntimeChange.Install && state.Ownership != RuntimeOwnership.Absent
                || frozen.Request.Change != RuntimeChange.Install && state.Ownership != RuntimeOwnership.Managed)
                return Conflict(before);
            var release = frozen.Expected!.Installer!;
            TailscaleMsiStaging.Lease? staged = null;
            try
            {
                if (frozen.Request.Change != RuntimeChange.Uninstall)
                {
                    staged = await TailscaleMsiStaging.Create().PrepareAsync(id, Rid, release.Version, request.DownloadProxy, lifetime.Token);
                    if (staged.Release != release || current is { Removed: false } && frozen.Request.Change == RuntimeChange.Update
                        && (!Version.TryParse(release.Version, out var next) || !Version.TryParse(current.Release.Version, out var old) || next <= old))
                        return Conflict(before);
                }
                else if (current is null || current.Release != release) return Conflict(before);
                var product = staged?.Evidence.Properties.ProductCode ?? current!.ProductCode;
                var intent = new Pending(id, frozen, current, product);
                var wasRunning = current is { Removed: false } && IsRunning();
                var boot = current is { Removed: false } ? StartMode() : ServiceStartMode.Automatic;
                var preferences = wasRunning ? await PreferencesAsync(lifetime.Token) : null;
                var identity = wasRunning ? await NodeIdAsync(lifetime.Token) : current?.NodeId;
                Write("pending.json", intent); // Never replay an uncertain MSI invocation.
                if (current is { Removed: false }) await StopAsync(lifetime.Token);
                var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Tailscale");
                var identityPath = Path.Combine(data, "server-state.conf");
                if (File.Exists(identityPath)) TailscaleControlProtection.Require(identityPath);
                var identityHash = File.Exists(identityPath) ? Hash(identityPath) : null;
                if (Directory.Exists(data)) BackupData(data, Path.Combine(Root, "data-" + id.ToString("N")), 0, new BackupBudget());
                var exit = await RunAsync(Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                    frozen.Request.Change == RuntimeChange.Uninstall ? ["/x", product, "/qn", "/norestart"]
                        : ["/i", staged!.Path, "/qn", "/norestart"], lifetime.Token);
                // A reboot request is pending recovery, never a verified successful install.
                if (exit != 0) return Conflict(await ObserveAsync(current, intent, id, lifetime.Token));
                var removed = frozen.Request.Change == RuntimeChange.Uninstall;
                if (identityHash is not null && (!File.Exists(identityPath) || removed && Hash(identityPath) != identityHash))
                    return Conflict(await ObserveAsync(current, intent, id, lifetime.Token));
                if (!removed)
                {
                    if (!await MatchesInstalledAsync(release.Version, product, lifetime.Token)) return Conflict(before);
                    await ConfigureServiceAsync(boot, current is { Removed: false } ? wasRunning : true, lifetime.Token);
                    if (preferences is not null && preferences != await PreferencesAsync(lifetime.Token)) return Conflict(before);
                    if (identity is not null && (wasRunning || current is not { Removed: false }) && identity != await NodeIdAsync(lifetime.Token)) return Conflict(before);
                }
                else if (!IsAbsent() || Directory.Exists(Path.Combine(Root, "data-" + id.ToString("N"))) && !Directory.Exists(data))
                    return Conflict(before);
                var commit = new Commit(id, frozen, release, product, removed ? "" : Hash(Cli), removed ? "" : Hash(Daemon), removed, identity);
                Write(ReceiptName(id), commit, overwrite: false); // Publish immutable proof before the active pointer.
                Write("current.json", commit);
                Delete("pending.json");
                return new(true, TailscaleInstallerSnapshot: await ObserveAsync(commit, null, id, lifetime.Token));
            }
            finally
            {
                try { staged?.Dispose(); }
                catch (IOException) when (Read<Commit>("current.json")?.OperationId == id) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or System.ComponentModel.Win32Exception or OperationCanceledException or RuntimePolicyException or System.Security.SecurityException)
        { return new(false, 65, ProblemCode: PrivilegedProblemCode.Conflict); }
    }

    private static bool Valid(ComponentExecuteRequest frozen, ComponentState state)
    {
        var request = frozen.Request;
        return TailscaleInstallerPlan.Evaluate(request, state, frozen.Expected?.Installer).Allowed
            && frozen.ComponentId == ComponentIds.Tailscale && request.ComponentId == ComponentIds.Tailscale && request.HostId == state.HostId
            && request.ExpectedRevision == state.Revision && request.Mode == ComponentSourceMode.NativeInstaller && request.KeepData
            && request.Change is RuntimeChange.Install or RuntimeChange.Update or RuntimeChange.Uninstall
            && request.SourceId is null && request.ReleaseId is null && request.ExpectedSourceRevision == 0 && request.ExpectedTrustRevision == 0
            && !string.IsNullOrWhiteSpace(request.IdempotencyKey) && request.IdempotencyKey.Length <= 128
            && frozen.Expected is { Mode: ComponentSourceMode.NativeInstaller, Archive: null, Package: null, Pin: null, Installer: not null }
            && frozen.Expected.Installer.Rid == Rid;
    }
    private static async Task<TailscaleInstallerSnapshot> ObserveAsync(Commit? current, Pending? pending, Guid operation, CancellationToken ct)
    {
        var physical = TailscaleWindowsInstallation.Create().Read(ct);
        var verified = current is { Disowned: false } && Read<Commit>(ReceiptName(current.OperationId)) == current
            && (current.Removed ? IsAbsent() : await MatchesInstalledAsync(current.Release.Version, current.ProductCode, ct)
                && Hash(Cli) == current.CliHash && Hash(Daemon) == current.ServiceHash);
        if (verified && pending?.OperationId == current?.OperationId) pending = null;
        var absent = physical.Kind == TailscaleWindowsInstallation.Kind.NoKnownArtifacts || current is { Removed: true } && verified;
        var ownership = physical.Kind == TailscaleWindowsInstallation.Kind.Unavailable ? RuntimeOwnership.Unknown
            : absent ? RuntimeOwnership.Absent : verified ? RuntimeOwnership.Managed : RuntimeOwnership.External;
        var recovery = pending is not null && (!verified || pending.OperationId != current?.OperationId)
            || current is { Disowned: false } && !verified;
        var version = ownership == RuntimeOwnership.Managed ? current!.Release.Version
            : physical.Registrations is { Count: 1 } registrations ? registrations[0].Version : null;
        var revision = BitConverter.ToInt64(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Join('|', ownership.ToString(), version, current?.OperationId.ToString(), pending?.OperationId.ToString(), recovery.ToString()))), 0) & long.MaxValue;
        if (revision == 0) revision = 1;
        var state = new ComponentState(ComponentIds.Tailscale, Environment.MachineName, revision, ownership, ComponentSourceMode.NativeInstaller,
            new("windows", Rid[4..], null, null, RuntimeInstallMode.NativeInstaller, "scm"), version, recovery);
        return new(new(ComponentIds.Tailscale, state, [new(ComponentSourceMode.NativeInstaller, "windows", true, null, "msi")],
            physical.ProblemCode is { } problem ? [problem] : [], CommittedOperationId: verified ? current!.OperationId : null,
            PendingOperationId: recovery ? pending?.OperationId ?? current?.OperationId : null),
            verified && current!.OperationId == operation ? operation : null, verified && !current!.Removed ? current.Release : null);
    }
    private static bool IsAbsent()
    {
        if (TailscaleWindowsInventory.Create().Read(default).Count != 0 || File.Exists(Cli) || File.Exists(Daemon)) return false;
        var services = ServiceController.GetServices();
        try { return services.All(service => !string.Equals(service.ServiceName, "Tailscale", StringComparison.OrdinalIgnoreCase)); }
        finally { foreach (var service in services) service.Dispose(); }
    }
    private static async Task<bool> MatchesInstalledAsync(string version, string product, CancellationToken ct)
    {
        var registrations = TailscaleWindowsInventory.Create().Read(ct);
        if (registrations.Count != 1 || registrations[0].ProductCode != product || registrations[0].Context != 4 || registrations[0].Advertised
            || !File.Exists(Cli) || !File.Exists(Daemon)) return false;
        TailscaleControlProtection.RequireExecutable(); TailscaleControlProtection.Require(Daemon);
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tailscale");
        var image = key?.GetValue("ImagePath") as string;
        if (image is null || !(image == Daemon || image == '"' + Daemon + '"' || image.StartsWith('"' + Daemon + "\" ", StringComparison.OrdinalIgnoreCase))) return false;
        var output = await RelaxKonOS.Server.Tailscale.TailscaleStatusCommand.RunFixedAsync(["version"], ct);
        return output.State == "observed" && output.Json?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split('-')[0] == version;
    }
    private static async Task<string> PreferencesAsync(CancellationToken ct)
    {
        var result = await RelaxKonOS.Server.Tailscale.TailscaleStatusCommand.RunFixedAsync(["get", "--json"], ct);
        if (result.State != "observed" || result.Json is null) throw new IOException("Tailscale preferences unavailable.");
        using var json = JsonDocument.Parse(result.Json);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(TailscaleNativeControl.Canonical(json.RootElement))));
    }
    private static async Task<string?> NodeIdAsync(CancellationToken ct)
    {
        var result = await RelaxKonOS.Server.Tailscale.TailscaleStatusCommand.RunFixedAsync(["status", "--json"], ct);
        if (result.State != "observed" || result.Json is null) throw new IOException("Tailscale identity unavailable.");
        using var json = JsonDocument.Parse(result.Json);
        return json.RootElement.TryGetProperty("Self", out var self) && self.ValueKind == JsonValueKind.Object
            && self.TryGetProperty("ID", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
    }
    private static async Task<PrivilegedOperationResult> RecoverAsync(PrivilegedOperationRequest request, Commit? current, Pending? pending, CancellationToken ct)
    {
        var maintenanceName = "maintenance-" + request.OperationId!.Value.ToString("N") + ".json";
        if (Read<Maintenance>(maintenanceName) is { } original)
        {
            if (original.OperationId != request.OperationId || original.Request != request.PackageRecovery) return Conflict(null);
            if (original.Request.Action == ComponentRecoveryAction.Close && pending?.OperationId == original.Request.OperationId) Delete("pending.json");
            return new(true);
        }
        if (pending is null || request.PackageRecovery is not { ComponentId: ComponentIds.Tailscale } recovery
            || recovery.OperationId != pending.OperationId || recovery.Action is not (ComponentRecoveryAction.Stop or ComponentRecoveryAction.Close)) return Conflict(null);
        // A cancelled wait does not cancel Windows Installer. Even an idle MSI service process
        // conservatively blocks recovery; never close or stop while an installer may still mutate.
        if (!MsiQuiescent()) return Conflict(null);
        if (recovery.Action == ComponentRecoveryAction.Stop)
        {
            var permitted = current is { Removed: false } && await MatchesInstalledAsync(current.Release.Version, current.ProductCode, ct)
                || await MatchesInstalledAsync(pending.Request.Expected!.Installer!.Version, pending.ProductCode, ct);
            if (!permitted) return Conflict(null);
            await StopAsync(ct);
            Write(maintenanceName, new Maintenance(request.OperationId.Value, recovery, DateTimeOffset.UtcNow), overwrite: false);
            return new(true);
        }
        if (!IsAbsent() && IsRunning()) return Conflict(null);
        var observed = await ObserveAsync(current, pending, request.OperationId!.Value, ct);
        Write("closed-" + request.OperationId.Value.ToString("N") + ".json", new { Request = recovery, Observation = observed, ClosedAt = DateTimeOffset.UtcNow }, overwrite: false);
        if (current is not null && observed.View.State?.Ownership != RuntimeOwnership.Managed) Write("current.json", current with { Disowned = true });
        if (current is null) Write("current.json", new Commit(pending.OperationId, pending.Request, pending.Request.Expected!.Installer!, "", "", "", false, null, true));
        Write(maintenanceName, new Maintenance(request.OperationId.Value, recovery, DateTimeOffset.UtcNow), overwrite: false);
        Delete("pending.json"); return new(true);
    }
    private static async Task<FileStream> AcquireAsync(CancellationToken ct)
    {
        var path = Path.Combine(TailscaleControlProtection.Root, "native.lock");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); TailscaleControlProtection.Require(path); return file; }
            catch (IOException) { await Task.Delay(100, ct); }
        }
    }
    private static bool MsiQuiescent()
    {
        var installers = System.Diagnostics.Process.GetProcessesByName("msiexec");
        try { return installers.Length == 0; }
        finally { foreach (var installer in installers) installer.Dispose(); }
    }
    private static string ReceiptName(Guid id) => "commit-" + id.ToString("N") + ".json";
    private static T? Read<T>(string name) where T : class
    {
        var path = Path.Combine(Root, name); if (!File.Exists(path)) return null;
        TailscaleControlProtection.Require(path); if (new FileInfo(path).Length > 262144) throw new IOException("Invalid installation record.");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path)); TailscaleNativeControl.Canonical(doc.RootElement);
        return doc.RootElement.Deserialize<T>(Json) ?? throw new IOException("Invalid installation record.");
    }
    private static void Write<T>(string name, T record, bool overwrite = true)
    {
        TailscaleControlProtection.Require(Root); var path = Path.Combine(Root, name); var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { TailscaleControlProtection.Require(temporary); JsonSerializer.Serialize(stream, record, Json); stream.Flush(true); }
            if (File.Exists(path)) TailscaleControlProtection.Require(path);
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Delete(string name) { var path = Path.Combine(Root, name); if (File.Exists(path)) { TailscaleControlProtection.Require(path); File.Delete(path); } }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static bool IsRunning() { using var service = new ServiceController("Tailscale"); return service.Status == ServiceControllerStatus.Running; }
    private static ServiceStartMode StartMode() { using var service = new ServiceController("Tailscale"); return service.StartType; }
    private static async Task StopAsync(CancellationToken ct)
    { using var service = new ServiceController("Tailscale"); if (service.Status != ServiceControllerStatus.Stopped) { service.Stop(); await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60)), ct); } }
    private static async Task ConfigureServiceAsync(ServiceStartMode mode, bool running, CancellationToken ct)
    {
        var start = mode switch { ServiceStartMode.Automatic => "auto", ServiceStartMode.Manual => "demand", ServiceStartMode.Disabled => "disabled", _ => throw new IOException("Invalid service startup type.") };
        if (await RunAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["config", "Tailscale", "start=", start], ct) != 0) throw new IOException("Service startup restore failed.");
        if (!running) await StopAsync(ct);
        else { using var service = new ServiceController("Tailscale"); if (service.Status != ServiceControllerStatus.Running) { service.Start(); await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60)), ct); } }
    }
    private static async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start(); await process.WaitForExitAsync(ct); return process.ExitCode;
    }
    private sealed class BackupBudget { public int Entries; public long Bytes; }
    private static void BackupData(string source, string destination, int depth, BackupBudget budget)
    {
        if (depth > 16) throw new IOException("Tailscale data nesting exceeds limit.");
        TailscaleControlProtection.Require(source); Directory.CreateDirectory(destination); TailscaleControlProtection.Require(destination);
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            if (++budget.Entries > 4096) throw new IOException("Tailscale backup entry count exceeds limit.");
            TailscaleControlProtection.Require(path); var target = Path.Combine(destination, Path.GetFileName(path));
            if (Directory.Exists(path)) BackupData(path, target, depth + 1, budget);
            else
            {
                var length = new FileInfo(path).Length; budget.Bytes += length;
                if (length > 128L * 1024 * 1024 || budget.Bytes > 512L * 1024 * 1024) throw new IOException("Tailscale backup size exceeds limit.");
                File.Copy(path, target, overwrite: false); TailscaleControlProtection.Require(target);
            }
        }
    }
    private static PrivilegedOperationResult Conflict(TailscaleInstallerSnapshot? snapshot) => new(false, 65,
        ProblemCode: PrivilegedProblemCode.Conflict, TailscaleInstallerSnapshot: snapshot);
}
