using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;

public static partial class PrivilegedOperationExecutor
{
    private sealed record PackageChangeReceipt(Guid OperationId, ComponentPackageChange Change,
        ComponentPackageTarget Target, bool WasRunning, bool WasEnabled, bool Committed);
    private static string PackageChangePath(HostPackageDefinition definition) => Path.Combine(ComponentPackageRoot, definition.ComponentId, "change.json");
    private static PackageChangeReceipt? ReadPackageChange(HostPackageDefinition definition)
    {
        var path = PackageChangePath(definition); RequirePackagePath(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 262144) throw new IOException("Invalid package change receipt.");
        var result = JsonSerializer.Deserialize<PackageChangeReceipt>(File.ReadAllBytes(path)) ?? throw new IOException("Invalid package change receipt.");
        if (result.OperationId == Guid.Empty || result.Change.PreviousOperationId == Guid.Empty
            || result.Change.Change is not (RuntimeChange.Update or RuntimeChange.Uninstall)
            || result.Target.PackageName != definition.PackageName || !AptPackageResolution.IsUsableExpectation(result.Target))
            throw new IOException("Invalid package change identity.");
        return result;
    }
    private static void WritePackageChange(HostPackageDefinition definition, PackageChangeReceipt receipt)
    {
        var path = PackageChangePath(definition); RequirePackagePath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, receipt); stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task<PrivilegedOperationResult> ChangeComponentPackageAsync(string? componentId,
        ComponentPackageTarget? expected, ComponentPackageChange? change, Guid? operationId)
    {
        if (!OperatingSystem.IsLinux() || HostPackageDefinitions.Find(componentId) is not { } definition
            || change is null || change.Change is not (RuntimeChange.Update or RuntimeChange.Uninstall)
            || change.PreviousOperationId == Guid.Empty || operationId is null || operationId == Guid.Empty
            || string.IsNullOrWhiteSpace(change.ExpectedInstalledVersion) || !AptPackageResolution.IsUsableExpectation(expected)
            || expected!.PackageName != definition.PackageName)
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "invalid package change");
        using var nativeGate = definition.ComponentId == ComponentIds.Tailscale
            ? await RelaxKonOS.PrivilegedHelper.TailscaleControlOperations.AcquireLifecycleAsync() : null;
        var directory = Path.GetDirectoryName(PackageReceiptPath(definition))!; RequirePackagePath(directory);
        if (!Directory.Exists(directory)) return Fail(65, PrivilegedProblemCode.Conflict, "managed package absent");
        var lockPath = Path.Combine(directory, "lock"); RequirePackagePath(lockPath);
        using var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var priorChange = ReadPackageChange(definition);
        if (priorChange?.OperationId == operationId)
        {
            if (priorChange.Change != change || !AptPackageResolution.MatchesExpectation(priorChange.Target, expected))
                return Fail(65, PrivilegedProblemCode.Conflict, "package change identity changed");
            var replay = await QueryComponentPackageAsync(componentId);
            return priorChange.Committed && replay.ComponentPackage is { RecoveryRequired: false, CommittedOperationId: var id } && id == operationId
                ? replay : Fail(65, PrivilegedProblemCode.Conflict, "package change requires recovery");
        }
        if (priorChange is { Committed: false }) return Fail(65, PrivilegedProblemCode.Conflict, "package recovery required");
        if (definition.ComponentId == ComponentIds.Tailscale && RelaxKonOS.PrivilegedHelper.TailscaleControlOperations.HasPendingOperations())
            return Fail(65, PrivilegedProblemCode.Conflict, "Tailscale operation requires recovery");
        var before = await QueryComponentPackageAsync(componentId);
        var original = ReadPackageReceipt(definition);
        if (before.ComponentPackage is not { Managed: true, RecoveryRequired: false } state
            || original is not { Committed: true } || original.OperationId != change.PreviousOperationId
            || state.InstalledVersion != change.ExpectedInstalledVersion || !await OwnsPackageServiceAsync(definition))
            return Fail(65, PrivilegedProblemCode.Conflict, "package ownership or revision changed");
        if (change.Change == RuntimeChange.Update)
        {
            if (!HostPackageDefinitions.SupportsVersion(definition.ComponentId, expected!.Version))
                return Fail(65, PrivilegedProblemCode.Conflict, "package version is outside the supported native configuration range");
            if (!AptPackageResolution.MatchesExpectation(expected, state.Candidate))
                return Fail(65, PrivilegedProblemCode.Conflict, "package candidate changed");
            var newer = await RunFixedCommandAsync("/usr/bin/dpkg", ["--compare-versions", expected.Version, "gt", state.InstalledVersion], TimeSpan.FromSeconds(30), "package is not an update");
            if (!newer.Success) return newer;
        }
        else if (!AptPackageResolution.MatchesExpectation(expected, original.Target))
            return Fail(65, PrivilegedProblemCode.Conflict, "installed package identity changed");
        var audit = await RunFixedCommandWithOutputAsync("/usr/bin/dpkg", ["--audit"], "package audit failed");
        if (!audit.Success || !string.IsNullOrWhiteSpace(DecodeUtf8(audit.OutputBase64)))
            return Fail(65, PrivilegedProblemCode.Conflict, "package database needs repair");
        using var domainGate = definition.ComponentId == ComponentIds.Squid ? OpenSquidPackageGate() : null;
        if (definition.ComponentId == ComponentIds.Squid && File.Exists("/etc/squid/relaxkonos.pending"))
            return Fail(65, PrivilegedProblemCode.Conflict, "configuration recovery required");
        var wasRunning = false; var wasEnabled = false;
        if (definition.Service.Length != 0)
        {
            var serviceState = await RunFixedCommandAsync("/usr/bin/systemctl", ["is-active", "--quiet", definition.Service], TimeSpan.FromSeconds(30), "service state unavailable");
            var bootState = await RunFixedCommandAsync("/usr/bin/systemctl", ["is-enabled", "--quiet", definition.Service], TimeSpan.FromSeconds(30), "boot state unavailable");
            if (!serviceState.Success && serviceState.ExitCode != 3 || !bootState.Success && bootState.ExitCode != 1)
                return Fail(65, PrivilegedProblemCode.Conflict, "service state unknown");
            wasRunning = serviceState.Success; wasEnabled = bootState.Success;
        }
        var pending = new PackageChangeReceipt(operationId.Value, change, expected, wasRunning, wasEnabled, false);
        WritePackageChange(definition, pending);
        if (change.Change == RuntimeChange.Uninstall)
        {
            if (definition.Service.Length != 0)
            {
            var stop = await RunFixedCommandAsync("/usr/bin/systemctl", ["stop", definition.Service], TimeSpan.FromSeconds(90), "package service stop failed");
            if (!stop.Success) return stop;
            }
            // dpkg refuses reverse dependencies and removes only this exact package. Never purge,
            // autoremove or let an APT resolver remove unrelated applications/data.
            var remove = await RunFixedCommandAsync("/usr/bin/dpkg", ["--remove", definition.PackageName], TimeSpan.FromMinutes(10), "package removal failed");
            if (!remove.Success) return remove;
            var (failure, removed) = await ResolveComponentPackageAsync(definition);
            if (failure is not null || removed?.InstalledVersion is not null || File.Exists(definition.Executable))
                return Fail(65, PrivilegedProblemCode.Conflict, "package removal verification failed");
            File.Delete(PackageReceiptPath(definition));
        }
        else
        {
            var updateArguments = new List<string> { "install", "--yes", "--no-remove", "--no-install-recommends", "-o", "Dpkg::Options::=--force-confold", definition.PackageName + "=" + expected.Version };
            updateArguments.AddRange(definition.ExtraPackages);
            var install = await RunAptAsync(updateArguments, TimeSpan.FromMinutes(10), "package update failed");
            if (!install.Success) return install;
            var (failure, updated) = await ResolveComponentPackageAsync(definition);
            if (failure is not null || updated?.InstalledVersion != expected.Version || !File.Exists(definition.Executable) || !await OwnsPackageServiceAsync(definition))
                return Fail(65, PrivilegedProblemCode.Conflict, "package update identity mismatch");
            RequirePackagePath(definition.Executable);
            var health = definition.ComponentId switch
            {
                ComponentIds.Nginx => await RunFixedCommandAsync(definition.Executable, ["-t"], TimeSpan.FromSeconds(30), "Nginx configuration invalid"),
                ComponentIds.Squid => await RunFixedCommandAsync(definition.Executable, ["-k", "parse"], TimeSpan.FromSeconds(30), "Squid configuration invalid"),
                ComponentIds.Samba => await RunFixedCommandAsync("/usr/bin/testparm", ["-s"], TimeSpan.FromSeconds(30), "Samba configuration invalid"),
                ComponentIds.SingBox or ComponentIds.Mihomo => await RunFixedCommandAsync(definition.Executable, ["version"], TimeSpan.FromSeconds(30), "runtime version unavailable"),
                ComponentIds.Tailscale => await RunFixedCommandAsync(definition.Executable, ["version"], TimeSpan.FromSeconds(30), "Tailscale version unavailable"),
                _ => await RunFixedCommandAsync(definition.Executable, ["--version"], TimeSpan.FromSeconds(30), "version unavailable")
            };
            if (!health.Success) return health;
            if (definition.Service.Length != 0)
            {
            var boot = await RunFixedCommandAsync("/usr/bin/systemctl", [wasEnabled ? "enable" : "disable", definition.Service], TimeSpan.FromSeconds(60), "boot state restore failed");
            if (!boot.Success) return boot;
            var service = await RunFixedCommandAsync("/usr/bin/systemctl", [wasRunning ? "restart" : "stop", definition.Service], TimeSpan.FromSeconds(90), "service state restore failed");
            if (!service.Success) return service;
            var running = (await RunFixedCommandAsync("/usr/bin/systemctl", ["is-active", "--quiet", definition.Service], TimeSpan.FromSeconds(30), "service health unavailable")).Success;
            if (running != wasRunning) return Fail(65, PrivilegedProblemCode.Conflict, "service health mismatch");
            }
            using var binary = File.OpenRead(definition.Executable);
            WritePackageReceipt(definition, new(definition.ComponentId, operationId.Value, expected, true, Convert.ToHexString(SHA256.HashData(binary))));
        }
        WritePackageChange(definition, pending with { Committed = true });
        return await QueryComponentPackageAsync(componentId);
    }
}
