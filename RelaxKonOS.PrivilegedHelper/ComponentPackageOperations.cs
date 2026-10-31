using System.Security.Cryptography;
using RelaxKonOS.PrivilegedHelper;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;

public static partial class PrivilegedOperationExecutor
{
    private const string ComponentPackageRoot = TailscalePackageInterlock.PackageRoot;
    private sealed record PackageReceipt(string ComponentId, Guid OperationId, ComponentPackageTarget Target,
        bool Committed, string? ExecutableSha256);

    private static void RequirePackagePath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("Component package paths cannot contain links.");
        if ((File.Exists(path) || Directory.Exists(path)) && Path.GetFullPath(path).StartsWith(ComponentPackageRoot, StringComparison.Ordinal)
            && (File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new UnauthorizedAccessException("Component package paths must be protected.");
    }
    private static string PackageReceiptPath(HostPackageDefinition definition) =>
        Path.Combine(ComponentPackageRoot, definition.ComponentId, "operation.json");
    private static PackageReceipt? ReadPackageReceipt(HostPackageDefinition definition)
    {
        var path = PackageReceiptPath(definition);
        RequirePackagePath(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 262144) throw new IOException("Invalid package receipt.");
        var receipt = JsonSerializer.Deserialize<PackageReceipt>(File.ReadAllBytes(path)) ?? throw new IOException("Invalid package receipt.");
        if (receipt.ComponentId != definition.ComponentId || receipt.OperationId == Guid.Empty
            || receipt.Target.PackageName != definition.PackageName || !AptPackageResolution.IsUsableExpectation(receipt.Target))
            throw new IOException("Invalid package receipt identity.");
        return receipt;
    }
    private static void WritePackageReceipt(HostPackageDefinition definition, PackageReceipt receipt)
    {
        var path = PackageReceiptPath(definition);
        RequirePackagePath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, receipt);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task<(PrivilegedOperationResult? Failure, ComponentPackageResolution? Resolution)> ResolveComponentPackageAsync(HostPackageDefinition definition)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/apt-get") || !File.Exists("/usr/bin/apt-cache")
            || definition.Service.Length != 0 && !Directory.Exists("/run/systemd/system"))
            return (Fail(64, PrivilegedProblemCode.UnsupportedOperation, "component package host is unsupported"), null);
        var policyRun = await RunFixedCommandWithOutputAsync("/usr/bin/apt-cache", ["policy", definition.PackageName], "package policy unavailable");
        if (policyRun.Success && policyRun.OutputBase64 is not null && string.IsNullOrWhiteSpace(DecodeUtf8(policyRun.OutputBase64)))
            return (null, new(definition.PackageName, null, null));
        if (!policyRun.Success || !AptPackageResolution.TryParsePolicy(DecodeUtf8(policyRun.OutputBase64), definition.PackageName, out var policy)
            || policy is null) return (Fail(65, PrivilegedProblemCode.InternalError, "component package policy unavailable"), null);
        ComponentPackageTarget? target = null;
        if (policy.CandidateVersion is { } version)
        {
            // Print the signed artifact identity even when the exact .deb is already cached/installed.
            // --print-uris does not create this new, nonexistent cache directory or run dpkg.
            var emptyCache = Path.Combine(ComponentPackageRoot, "resolution-" + Guid.NewGuid().ToString("N"));
            var arguments = new List<string> { "-o", "Acquire::ForceHash=SHA256", "-o", "Dir::Cache::archives=" + emptyCache,
                "install", "--print-uris", "--reinstall", "--yes", "--no-remove", "--no-install-recommends", definition.PackageName + "=" + version };
            arguments.AddRange(definition.ExtraPackages);
            var plan = await RunFixedCommandWithOutputAsync("/usr/bin/apt-get", arguments, "package resolution unavailable");
            if (plan.Success && AptPackageResolution.TryParseUriPlan(DecodeUtf8(plan.OutputBase64), definition.PackageName, version, out var main, out var dependencies))
                AptPackageResolution.TryBuildTarget(policy, main, dependencies, out target, out _);
        }
        var updateAvailable = policy.InstalledVersion is { } installedVersion && target is not null
            && (await RunFixedCommandAsync("/usr/bin/dpkg", ["--compare-versions", target.Version, "gt", installedVersion], TimeSpan.FromSeconds(30), "package update comparison unavailable")).Success;
        return (null, new(definition.PackageName, policy.InstalledVersion, target, CandidateVersion: policy.CandidateVersion, UpdateAvailable: updateAvailable));
    }
    private static async Task<PrivilegedOperationResult> QueryComponentPackageAsync(string? componentId, Guid? receiptOperationId = null)
    {
        if (HostPackageDefinitions.Find(componentId) is not { } definition)
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "unknown component package");
        var (failure, resolution) = await ResolveComponentPackageAsync(definition);
        if (failure is not null) return failure;
        try
        {
            var receipt = ReadPackageReceipt(definition);
            var change = ReadPackageChange(definition);
            ComponentRecoveryReceipt? recovered = null;
            if (receiptOperationId is { } recoveryId)
            {
                var recoveryPath = Path.Combine(ComponentPackageRoot, definition.ComponentId, "recoveries", recoveryId.ToString("N") + ".json");
                RequirePackagePath(recoveryPath);
                if (File.Exists(recoveryPath))
                {
                    if (new FileInfo(recoveryPath).Length > 4096) throw new IOException("Invalid recovery receipt");
                    recovered = JsonSerializer.Deserialize<ComponentRecoveryReceipt>(await File.ReadAllBytesAsync(recoveryPath));
                    if (recovered?.OperationId != recoveryId || recovered.TargetOperationId == Guid.Empty || !Enum.IsDefined(recovered.Action))
                        throw new IOException("Invalid recovery identity");
                }
            }
            var identity = await VerifyInstalledPackageAsync(definition);
            var registration = ReadPackageRegistration(definition);
            var authorized = registration?.Identity ?? (receipt is { Committed: true, ExecutableSha256: not null }
                ? new ComponentInstalledPackage(definition.PackageName, receipt.Target.Version, receipt.Target.Architecture, receipt.ExecutableSha256) : null);
            var ownership = PackageIdentityPolicy.Assess(authorized, identity, receipt is { Committed: false } || change is { Committed: false });
            var managed = ownership.Managed;
            var externalChange = ownership.ExternalChange;
            var exactCommit = receipt is { Committed: true, ExecutableSha256: not null }
                && PackageIdentityPolicy.ProvesOriginalCommit(new(definition.PackageName, receipt.Target.Version, receipt.Target.Architecture, receipt.ExecutableSha256), identity);
            if (receiptOperationId is { } registrationId && registration?.OperationId == registrationId)
                recovered = new(registrationId, Guid.Empty, ComponentRecoveryAction.RegisterPackage);
            // A binary outside the selected package is external, never an absence proof.
            if (resolution!.InstalledVersion is null && File.Exists(definition.Executable))
                resolution = resolution with { InstalledVersion = "external" };
            if (resolution.InstalledVersion is null && definition.ComponentId is ComponentIds.SingBox or ComponentIds.Mihomo
                && File.Exists("/usr/local/bin/" + definition.PackageName))
                resolution = resolution with { InstalledVersion = "external" };
            var policyMissing = definition.ComponentId == ComponentIds.Docker && !DockerPackagePolicyReady()
                || definition.ComponentId == ComponentIds.SingBox && !SingBoxPackagePolicyReady()
                || definition.ComponentId == ComponentIds.Tailscale && (!TailscalePackagePolicyReady()
                    || resolution.Candidate is { } candidate && !TailscalePackageCandidateAllowed(candidate));
            return new(true, ComponentPackage: resolution with {
                Candidate = policyMissing ? null : resolution.Candidate,
                BlockingProblem = policyMissing ? ComponentProblemCodes.HostPolicyRequired : null,
                InstalledTarget = exactCommit ? receipt!.Target : null,
                InstalledPackage = identity, ExternalPackageChange = externalChange, RecordedPackageVersion = authorized?.Version,
                Managed = managed, RecoveryRequired = ownership.RecoveryRequired
                    || definition.ComponentId == ComponentIds.Squid && File.Exists("/etc/squid/relaxkonos.pending"),
                CommittedOperationId = change is { Committed: true, Change.Change: RuntimeChange.Uninstall } && resolution.InstalledVersion is null ? change.OperationId : exactCommit ? receipt!.OperationId : change is { Committed: true, ResultIdentity: not null } && change.ResultIdentity == identity ? change.OperationId : null,
                PendingOperationId = change is { Committed: false } ? change.OperationId : (receipt is not null || registration is not null) && !managed ? (receipt?.OperationId ?? registration!.OperationId) : null, RecoveryReceipt = recovered });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return Fail(65, PrivilegedProblemCode.Conflict, "component package ownership requires inspection"); }
    }
    private static async Task<PrivilegedOperationResult> InstallComponentPackageAsync(string? componentId, ComponentPackageTarget? expected, Guid? operationId)
    {
        if (!OperatingSystem.IsLinux() || HostPackageDefinitions.Find(componentId) is not { } definition
            || operationId is null || operationId == Guid.Empty || !AptPackageResolution.IsUsableExpectation(expected)
            || expected!.PackageName != definition.PackageName)
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "component package installation request invalid");
        using var nativeGate = definition.ComponentId == ComponentIds.Tailscale
            ? await TailscaleControlOperations.AcquireLifecycleAsync() : null;
        RequirePackagePath(ComponentPackageRoot);
        Directory.CreateDirectory(ComponentPackageRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var directory = Path.GetDirectoryName(PackageReceiptPath(definition))!;
        RequirePackagePath(directory);
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var lockPath = Path.Combine(directory, "lock"); RequirePackagePath(lockPath);
        using var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (ReadPackageChange(definition) is { Committed: false }) return Fail(65, PrivilegedProblemCode.Conflict, "component package change recovery required");
        var original = ReadPackageReceipt(definition);
        if (original is not null)
        {
            if (original.OperationId != operationId || !AptPackageResolution.MatchesExpectation(original.Target, expected))
                return Fail(65, PrivilegedProblemCode.Conflict, "component package operation already exists");
            var replay = await QueryComponentPackageAsync(componentId);
            return replay.ComponentPackage is { Managed: true, CommittedOperationId: var committed } && committed == operationId
                ? replay : Fail(65, PrivilegedProblemCode.Conflict, "component package recovery required");
        }
        foreach (var systemDirectory in definition.Service.Length == 0 ? Array.Empty<string>() : new[] { "/etc/systemd/system", "/run/systemd/system" })
            if (File.Exists(Path.Combine(systemDirectory, definition.Service)) && !IsOwnedRemovedPackageMask(definition, Path.Combine(systemDirectory, definition.Service))
                || Directory.Exists(Path.Combine(systemDirectory, definition.Service + ".d")))
                return Fail(65, PrivilegedProblemCode.Conflict, "external service overrides require review");
        if (definition.ComponentId == ComponentIds.Tailscale && TailscaleControlOperations.HasPendingOperations())
            return Fail(65, PrivilegedProblemCode.Conflict, "Tailscale operation requires recovery");
        if (definition.ComponentId == ComponentIds.Docker && !DockerPackagePolicyReady())
            return Fail(65, PrivilegedProblemCode.AccessDenied, "Docker trusted repository and access policy require setup");
        if (definition.ComponentId == ComponentIds.Tailscale && !TailscalePackagePolicyReady())
            return Fail(65, PrivilegedProblemCode.AccessDenied, "Tailscale official repository requires setup");
        if (definition.ComponentId == ComponentIds.SingBox && !SingBoxPackagePolicyReady())
            return Fail(65, PrivilegedProblemCode.AccessDenied, "sing-box official repository requires setup");
        if (!HostPackageDefinitions.SupportsVersion(definition.ComponentId, expected!.Version))
            return Fail(65, PrivilegedProblemCode.Conflict, "package version is outside the supported native configuration range");
        var update = await RunAptAsync(["update"], TimeSpan.FromMinutes(10), "component package index refresh failed");
        if (!update.Success) return update;
        var before = await QueryComponentPackageAsync(componentId);
        if (before.ComponentPackage is not { InstalledVersion: null, RecoveryRequired: false, Candidate: not null } state
            || !AptPackageResolution.MatchesExpectation(expected, state.Candidate))
            return Fail(65, PrivilegedProblemCode.Conflict, "component package frozen state changed");
        using var domainGate = definition.ComponentId == ComponentIds.Squid ? OpenSquidPackageGate() : null;
        if (definition.ComponentId == ComponentIds.Squid && File.Exists("/etc/squid/relaxkonos.pending"))
            return Fail(65, PrivilegedProblemCode.Conflict, "Squid configuration recovery required");
        // Durable intent precedes the first package mutation. An interrupted operation blocks replacement.
        var pending = new PackageReceipt(definition.ComponentId, operationId.Value, expected, false, null);
        WritePackageReceipt(definition, pending);
        var arguments = new List<string> { "install", "--yes", "--no-remove", "--no-install-recommends", "-o", "Dpkg::Options::=--force-confold", definition.PackageName + "=" + expected.Version };
        arguments.AddRange(definition.ExtraPackages);
        var install = await RunAptAsync(arguments, TimeSpan.FromMinutes(10), "component package install failed");
        if (!install.Success) return install;
        var (failure, after) = await ResolveComponentPackageAsync(definition);
        if (failure is not null || after?.InstalledVersion != expected.Version || !File.Exists(definition.Executable))
            return Fail(66, PrivilegedProblemCode.InternalError, "component package installed identity mismatch");
        RequirePackagePath(definition.Executable);
        var health = definition.ComponentId switch
        {
            ComponentIds.Nginx => await RunFixedCommandAsync(definition.Executable, ["-t"], TimeSpan.FromSeconds(30), "Nginx configuration invalid"),
            ComponentIds.Squid => await RunFixedCommandAsync(definition.Executable, ["-k", "parse"], TimeSpan.FromSeconds(30), "Squid configuration invalid"),
            ComponentIds.Samba => await RunFixedCommandAsync("/usr/bin/testparm", ["-s"], TimeSpan.FromSeconds(30), "Samba configuration invalid"),
            ComponentIds.SingBox or ComponentIds.Mihomo => await RunFixedCommandAsync(definition.Executable, ["version"], TimeSpan.FromSeconds(30), "runtime version unavailable"),
            ComponentIds.Tailscale => await RunFixedCommandAsync(definition.Executable, ["version"], TimeSpan.FromSeconds(30), "Tailscale version unavailable"),
            _ => await RunFixedCommandAsync(definition.Executable, ["--version"], TimeSpan.FromSeconds(30), "Docker version unavailable")
        };
        if (!health.Success) return health;
        if (!await OwnsPackageServiceAsync(definition)) return Fail(65, PrivilegedProblemCode.Conflict, "component service is external");
        if (definition.Service.Length != 0)
        {
        var service = await RunFixedCommandAsync("/usr/bin/systemctl", ["enable", "--now", definition.Service], TimeSpan.FromSeconds(60), "component service activation failed");
        if (!service.Success) return service;
        var running = await RunFixedCommandAsync("/usr/bin/systemctl", ["is-active", "--quiet", definition.Service], TimeSpan.FromSeconds(30), "component service unhealthy");
        if (!running.Success) return running;
        }
        if (definition.ComponentId == ComponentIds.Docker)
        {
            var grant = await RunFixedCommandAsync("/usr/sbin/usermod", ["--append", "--groups", "docker", ReadDockerAccessUser()!],
                TimeSpan.FromSeconds(30), "Docker access grant failed");
            if (!grant.Success) return grant;
        }
        using var executable = File.OpenRead(definition.Executable);
        WritePackageReceipt(definition, pending with { Committed = true, ExecutableSha256 = Convert.ToHexString(SHA256.HashData(executable)) });
        return await QueryComponentPackageAsync(componentId);
    }
    private static bool DockerPackagePolicyReady()
    {
        if (ReadDockerAccessUser() is null) return false;
        const string sourcePath = "/etc/apt/sources.list.d/relaxkonos-docker.sources";
        const string keyPath = "/etc/apt/keyrings/relaxkonos-docker.asc";
        RequirePackagePath(sourcePath); RequirePackagePath(keyPath);
        if (!File.Exists(sourcePath) || !File.Exists(keyPath)) return false;
        var release = File.ReadAllLines("/etc/os-release").Where(line => line.Contains('='))
            .Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1].Trim('"'));
        if (!release.TryGetValue("ID", out var distro) || !release.TryGetValue("VERSION_ID", out var version)
            || LinuxDistributionSupport.ResolveDockerRepository(distro, version) is not { } repository) return false;
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch {
            System.Runtime.InteropServices.Architecture.X64 => "amd64", System.Runtime.InteropServices.Architecture.Arm64 => "arm64", _ => null };
        var (repositoryDistro, suite) = repository;
        var expected = $"# RelaxKonOS managed Docker Engine\nTypes: deb\nURIs: https://download.docker.com/linux/{repositoryDistro}\nSuites: {suite}\nComponents: stable\nArchitectures: {architecture}\nSigned-By: {keyPath}\n";
        return architecture is not null && File.ReadAllText(sourcePath) == expected;
    }
    private static FileStream OpenSquidPackageGate()
    {
        RequirePackagePath("/etc/squid");
        Directory.CreateDirectory("/etc/squid");
        const string path = "/etc/squid/relaxkonos.lock"; RequirePackagePath(path);
        var gate = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return gate;
    }
    private static async Task<bool> OwnsPackageServiceAsync(HostPackageDefinition definition)
    {
        if (definition.Service.Length == 0)
        {
            var binaryOwner = await RunFixedCommandWithOutputAsync("/usr/bin/dpkg-query", ["-S", definition.Executable], "binary ownership unavailable");
            return binaryOwner.Success && DecodeUtf8(binaryOwner.OutputBase64).StartsWith(definition.PackageName + ":", StringComparison.Ordinal);
        }
        foreach (var systemDirectory in definition.Service.Length == 0 ? Array.Empty<string>() : new[] { "/etc/systemd/system", "/run/systemd/system" })
            if (File.Exists(Path.Combine(systemDirectory, definition.Service)) || Directory.Exists(Path.Combine(systemDirectory, definition.Service + ".d"))) return false;
        var show = await RunFixedCommandWithOutputAsync("/usr/bin/systemctl", ["show", "--property=FragmentPath", "--value", definition.Service], "service ownership unavailable");
        var path = DecodeUtf8(show.OutputBase64).Trim();
        if (!show.Success || !(path.StartsWith("/usr/lib/systemd/system/", StringComparison.Ordinal)
            || path.StartsWith("/lib/systemd/system/", StringComparison.Ordinal)) || path.Contains('\n')) return false;
        var owner = await RunFixedCommandWithOutputAsync("/usr/bin/dpkg-query", ["-S", path], "service package ownership unavailable");
        var owners = definition.ComponentId == ComponentIds.Nginx ? new[] { "nginx", "nginx-common" } : new[] { definition.PackageName };
        if (owner.Success && owners.Any(package => DecodeUtf8(owner.OutputBase64).StartsWith(package + ":", StringComparison.Ordinal))) return true;
        // usrmerge can expose the same unit under /lib and /usr/lib while dpkg retains one spelling.
        var alias = path.StartsWith("/usr/lib/", StringComparison.Ordinal) ? path[4..] : "/usr" + path;
        if (!File.Exists(alias)) return false;
        var actualIdentity = await RunFixedCommandWithOutputAsync("/usr/bin/stat", ["-L", "--format=%d:%i", path], "unit identity unavailable");
        var aliasIdentity = await RunFixedCommandWithOutputAsync("/usr/bin/stat", ["-L", "--format=%d:%i", alias], "unit alias identity unavailable");
        if (!actualIdentity.Success || !aliasIdentity.Success || DecodeUtf8(actualIdentity.OutputBase64) != DecodeUtf8(aliasIdentity.OutputBase64)) return false;
        owner = await RunFixedCommandWithOutputAsync("/usr/bin/dpkg-query", ["-S", alias], "service alias ownership unavailable");
        return owner.Success && owners.Any(package => DecodeUtf8(owner.OutputBase64).StartsWith(package + ":", StringComparison.Ordinal));
    }

    private static bool IsOwnedRemovedPackageMask(HostPackageDefinition definition, string unit)
    {
        // Debian's postrm masks a removed package; its next preinst unsets that mask. Only our
        // verified removal witness may authorize this shape. Administrator overrides remain refused.
        if (definition.ComponentId != ComponentIds.Tailscale || unit != "/etc/systemd/system/" + definition.Service
            || ReadPackageChange(definition) is not { Committed: true, Change.Change: RuntimeChange.Uninstall }) return false;
        var marker = "/var/lib/systemd/deb-systemd-helper-masked/" + definition.Service;
        RequirePackagePath(Path.GetDirectoryName(unit)!); RequirePackagePath(marker);
        return File.Exists(marker) && new FileInfo(marker).Length == 0 && new FileInfo(unit).LinkTarget == "/dev/null";
    }

    private static async Task<PrivilegedOperationResult> RecoverComponentPackageAsync(ComponentRecoveryRequest? request, Guid? operationId)
    {
        if (!OperatingSystem.IsLinux() || request is null || operationId is null || operationId == Guid.Empty
            || !Enum.IsDefined(request.Action))
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "component recovery request invalid");
        var definition = HostPackageDefinitions.Find(request.ComponentId);
        if (request.Action == ComponentRecoveryAction.RegisterPackage)
            return await RegisterComponentPackageAsync(request, operationId.Value);
        if (request.ExpectedInstalledPackage is not null)
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "unexpected registration expectation");
        if (request.Action == ComponentRecoveryAction.ConfigureOfficialSource)
        {
            if (request.OperationId != Guid.Empty) return Fail(64, PrivilegedProblemCode.InvalidRequest, "invalid official source request");
            if (request.ComponentId == ComponentIds.SingBox)
            {
                await ConfigureSingBoxPackageSourceAsync();
                return await RunAptAsync(["update"], TimeSpan.FromMinutes(10), "official package source refresh failed");
            }
            if (request.ComponentId != ComponentIds.Tailscale)
                return Fail(64, PrivilegedProblemCode.InvalidRequest, "invalid official source request");
            await ConfigureTailscalePackageSourceAsync();
            return await RunAptAsync(["update"], TimeSpan.FromMinutes(10), "official package source refresh failed");
        }
        if (request.Action == ComponentRecoveryAction.RefreshSources)
        {
            if (request.OperationId != Guid.Empty || definition is null && request.ComponentId != ComponentIds.Nginx)
                return Fail(64, PrivilegedProblemCode.InvalidRequest, "component source request invalid");
            return await RunAptAsync(["update"], TimeSpan.FromMinutes(10), "component package index refresh failed");
        }
        if (request.OperationId == Guid.Empty || definition is null)
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "component recovery request invalid");
        using var nativeGate = definition.ComponentId == ComponentIds.Tailscale
            ? await TailscaleControlOperations.AcquireLifecycleAsync() : null;
        var directory = Path.Combine(ComponentPackageRoot, definition.ComponentId);
        RequirePackagePath(directory);
        if (!Directory.Exists(directory)) return Fail(65, PrivilegedProblemCode.Conflict, "component recovery target absent");
        var lockPath = Path.Combine(directory, "lock"); RequirePackagePath(lockPath);
        using var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var recoveries = Path.Combine(directory, "recoveries"); RequirePackagePath(recoveries);
        Directory.CreateDirectory(recoveries, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var receiptPath = Path.Combine(recoveries, operationId.Value.ToString("N") + ".json"); RequirePackagePath(receiptPath);
        var result = new ComponentRecoveryReceipt(operationId.Value, request.OperationId, request.Action);
        if (File.Exists(receiptPath))
        {
            var original = JsonSerializer.Deserialize<ComponentRecoveryReceipt>(await File.ReadAllBytesAsync(receiptPath));
            return original == result ? new(true) : Fail(65, PrivilegedProblemCode.Conflict, "component recovery identity changed");
        }
        var pendingChange = ReadPackageChange(definition);
        if (pendingChange is { Committed: false } && pendingChange.OperationId == request.OperationId)
        {
            if (request.Action == ComponentRecoveryAction.Stop && definition.Service.Length != 0)
            {
                if (!await OwnsPackageServiceAsync(definition)) return Fail(65, PrivilegedProblemCode.Conflict, "service ownership unavailable");
                var stoppedChange = await RunFixedCommandAsync("/usr/bin/systemctl", ["stop", definition.Service], TimeSpan.FromSeconds(90), "service stop failed");
                if (!stoppedChange.Success) return stoppedChange;
            }
            if (definition.Service.Length != 0 && (await RunFixedCommandAsync("/usr/bin/systemctl", ["is-active", "--quiet", definition.Service], TimeSpan.FromSeconds(30), "service state unavailable")).Success)
                return Fail(65, PrivilegedProblemCode.Conflict, "service must be stopped before closing");
            if (request.Action == ComponentRecoveryAction.Close)
            {
                var changeAudit = await RunFixedCommandWithOutputAsync("/usr/bin/dpkg", ["--audit"], "package audit unavailable");
                if (!changeAudit.Success || !string.IsNullOrWhiteSpace(DecodeUtf8(changeAudit.OutputBase64)))
                    return Fail(65, PrivilegedProblemCode.Conflict, "package repair required");
                var closedChange = Path.Combine(directory, "closed-change-" + request.OperationId.ToString("N") + ".json");
                RequirePackagePath(closedChange);
                File.Move(PackageChangePath(definition), closedChange);
                // Operator closure must not turn a partially committed update into original success.
                var currentReceipt = ReadPackageReceipt(definition);
                var currentFacts = await QueryComponentPackageAsync(request.ComponentId);
                if (currentReceipt is not null && (currentReceipt.OperationId == request.OperationId || currentFacts.ComponentPackage?.Managed != true))
                {
                    var closedReceipt = Path.Combine(directory, "closed-" + request.OperationId.ToString("N") + ".json"); RequirePackagePath(closedReceipt);
                    File.Move(PackageReceiptPath(definition), closedReceipt);
                }
            }
            using var changeProof = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            File.SetUnixFileMode(receiptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            JsonSerializer.Serialize(changeProof, result); changeProof.Flush(true);
            return new(true);
        }
        var registered = ReadPackageRegistration(definition);
        if (ReadPackageReceipt(definition) is null && registered?.OperationId == request.OperationId)
        {
            var facts = await QueryComponentPackageAsync(request.ComponentId);
            if (facts.ComponentPackage is not { RecoveryRequired: true })
                return Fail(65, PrivilegedProblemCode.Conflict, "healthy registration cannot be closed as unfinished");
            if (request.Action == ComponentRecoveryAction.Stop && definition.Service.Length != 0)
            {
                if (!await OwnsPackageServiceAsync(definition)) return Fail(65, PrivilegedProblemCode.Conflict, "service identity unverified");
                var stop = await RunFixedCommandAsync("/usr/bin/systemctl", ["disable", "--now", definition.Service], TimeSpan.FromSeconds(90), "service stop failed");
                if (!stop.Success) return stop;
            }
            if (definition.Service.Length != 0 && (await RunFixedCommandAsync("/usr/bin/systemctl", ["is-active", "--quiet", definition.Service], TimeSpan.FromSeconds(30), "service state unavailable")).Success)
                return Fail(65, PrivilegedProblemCode.Conflict, "service must be stopped before closing");
            if (request.Action == ComponentRecoveryAction.Close)
                File.Move(RegistrationPath(definition), Path.Combine(directory, "closed-registration-" + request.OperationId.ToString("N") + ".json"));
            using var proof = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            File.SetUnixFileMode(receiptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            JsonSerializer.Serialize(proof, result); proof.Flush(true);
            return new(true);
        }
        var pending = ReadPackageReceipt(definition);
        if (pending is null || pending.OperationId != request.OperationId)
            return Fail(65, PrivilegedProblemCode.Conflict, "component recovery identity changed");
        if (pending.Committed)
        {
            var state = await QueryComponentPackageAsync(request.ComponentId);
            if (state.ComponentPackage is not { Managed: false, RecoveryRequired: true })
                return Fail(65, PrivilegedProblemCode.Conflict, "healthy committed installation cannot be closed as unfinished");
        }
        if (request.Action == ComponentRecoveryAction.Stop && definition.Service.Length != 0)
        {
            if (!await OwnsPackageServiceAsync(definition)) return Fail(65, PrivilegedProblemCode.Conflict, "component service ownership unverified");
            var stopped = await RunFixedCommandAsync("/usr/bin/systemctl", ["disable", "--now", definition.Service], TimeSpan.FromSeconds(90), "component emergency stop failed");
            if (!stopped.Success) return stopped;
        }
        var running = await RunFixedCommandAsync("/usr/bin/systemctl", ["is-active", "--quiet", definition.Service], TimeSpan.FromSeconds(30), "component service still running");
        if (definition.Service.Length != 0 && running.Success) return Fail(65, PrivilegedProblemCode.Conflict, "component service still running");
        if (request.Action == ComponentRecoveryAction.Close)
        {
            var audit = await RunFixedCommandWithOutputAsync("/usr/bin/dpkg", ["--audit"], "package repair required");
            if (!audit.Success || !string.IsNullOrWhiteSpace(DecodeUtf8(audit.OutputBase64)))
                return Fail(65, PrivilegedProblemCode.Conflict, "package repair required");
            var closed = Path.Combine(directory, "closed-" + request.OperationId.ToString("N") + ".json"); RequirePackagePath(closed);
            if (File.Exists(closed)) return Fail(65, PrivilegedProblemCode.Conflict, "component closure already exists");
            File.Move(PackageReceiptPath(definition), closed); // Preserve original intent; do not remove packages or claim installation success.
        }
        using (var stream = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            File.SetUnixFileMode(receiptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            JsonSerializer.Serialize(stream, result); stream.Flush(true);
        }
        return new(true);
    }

}
