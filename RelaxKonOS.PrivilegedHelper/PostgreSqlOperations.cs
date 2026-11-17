using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.ManagedRuntimes.PostgreSql;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;

public static partial class PrivilegedOperationExecutor
{
    private const string PgRoot = PostgreSqlInitializationPlanner.ControlRoot;
    private const string PgCreateConfig = "/etc/postgresql-common/createcluster.conf";
    private const string PgSuppression = "# RelaxKonOS pending PostgreSQL installation\ncreate_main_cluster = false\n";
    private static readonly SemaphoreSlim PgGate = new(1, 1);
    private static readonly JsonSerializerOptions PgJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record PgPending(Guid Id, string Identity, PostgreSqlInstallationRequest Request, string Phase, bool RestartAfterBackup=false);
    private sealed record PgReceipt(Guid Id, string Identity);
    private sealed record PgLedger(long Revision, ComponentPackageTarget? Package, string? BinaryHash, string? ServiceHash,
        Guid? Instance, int Port, string? ConfigurationHash, PgPending? Pending, IReadOnlyList<PgReceipt> Receipts,
        [property: JsonRequired] IReadOnlyList<PostgreSqlDatabase> Databases, [property: JsonRequired] IReadOnlyList<PostgreSqlAccount> Accounts, PostgreSqlRemoteCommand? RemoteAccess = null, bool PackageRemoved = false, IReadOnlyList<PostgreSqlBackup>? Backups = null, bool DataDeleted = false, PostgreSqlBackupSchedule? BackupSchedule=null, IReadOnlyList<PostgreSqlBackup>? BackupDeletions=null, Guid? LedgerInstance=null);
    private sealed record PgCommandResult(int Code, string Output);
    private sealed class PgFailure(string code) : Exception(code);

    private static async Task<PrivilegedOperationResult> ExecutePostgreSqlAsync(PrivilegedOperationRequest request)
    {
        if (request != new PrivilegedOperationRequest(request.Operation, OperationId: request.OperationId,
            Correlation: request.Correlation, PostgreSql: request.PostgreSql, PostgreSqlSecrets: request.PostgreSqlSecrets, PostgreSqlTls: request.PostgreSqlTls, PostgreSqlInstance: request.PostgreSqlInstance))
            return new(false, 64, Error: "database.request_invalid", ProblemCode: PrivilegedProblemCode.InvalidRequest);
        if (request.Operation is (PrivilegedOperationKind.PostgreSqlQuery or PrivilegedOperationKind.PostgreSqlMaintenancePlan or PrivilegedOperationKind.PostgreSqlBackupScheduleQuery or PrivilegedOperationKind.PostgreSqlMigrationPlan) && (request.PostgreSql is not null || request.PostgreSqlSecrets is not null || request.PostgreSqlTls is not null))
            return new(false, 64, Error: "database.request_invalid", ProblemCode: PrivilegedProblemCode.InvalidRequest);
        if (!OperatingSystem.IsLinux() || !File.Exists("/etc/os-release")) return PgReply(PgUnsupported());
        var release = File.ReadAllLines("/etc/os-release");
        if (!release.Contains("ID=ubuntu") || !release.Contains("VERSION_ID=\"24.04\"")
            || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64
            || !Directory.Exists("/run/systemd/system")) return PgReply(PgUnsupported());
        await PgGate.WaitAsync();
        try
        {
            PgNoLinks(PgRoot);
            Directory.CreateDirectory(PgRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if ((await PgRun("/usr/bin/stat", ["-c", "%u", PgRoot])).Output.Trim() != "0") throw new PgFailure("database.state_untrusted");
            File.SetUnixFileMode(PgRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            PgNoLinks(PgRoot + "/lock");
            using var gate = new FileStream(PgRoot + "/lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var primary = PgRead();
            var instances = PgReadAdditional();
            var id = request.OperationId!.Value;
            var ledger = PgSelectLedger(primary, instances, request, id);
            if(request.Operation==PrivilegedOperationKind.PostgreSqlBackupScheduleQuery)
                return PgReply(new(true,ledger.Package is not null&&!ledger.PackageRemoved,ledger.Package is not null,ledger.Package?.Version,ledger.Revision,ledger.Instance,ledger.Port,false,false,
                    ledger.Pending?.Id,null,null,PendingMaintenance:ledger.Pending?.Request.Maintenance,BackupSchedule:ledger.BackupSchedule));
            if (request.Operation == PrivilegedOperationKind.PostgreSqlMigrationPlan)
                return PgReply((await PgObserve(ledger,id)) with { Candidate = await PgResolveMajor() });
            if (request.Operation == PrivilegedOperationKind.PostgreSqlMaintenancePlan)
                return PgReply((await PgObserve(ledger,id)) with { Candidate = await PgResolveMaintenance(ledger) });
            if (request.Operation == PrivilegedOperationKind.PostgreSqlQuery)
                return PgReply((await PgObserve(ledger, id)) with { Instances = PgInstanceSummaries(primary, instances) });
            if (request.PostgreSql is not { Confirmed: true } options) throw new PgFailure("database.confirmation_required");
            if (request.Operation == PrivilegedOperationKind.PostgreSqlRecover)
            {
                if (options.RecoverOperationId is not { } original || original == Guid.Empty || original == id
                    || options.Command is not null || options.FrozenPackage is not null || options.AccountCommand is not null || options.AdminRecovery is not null || options.RemoteAccess is not null || options.Maintenance is not null) throw new PgFailure("database.request_invalid");
                var recoveryIdentity = PgHash(JsonSerializer.SerializeToUtf8Bytes(options, PgJson));
                if (ledger.Receipts.FirstOrDefault(x => x.Id == id) is { } priorRecovery && priorRecovery.Identity != recoveryIdentity)
                    throw new PgFailure("database.operation_conflict");
                if (ledger.Pending is null)
                {
                    if (!ledger.Receipts.Any(x => x.Id == original)) throw new PgFailure("database.operation_unknown");
                    ledger = PgRecoveryReceipt(ledger, id, options);
                    return PgReply(await PgObserve(ledger, original));
                }
                if (ledger.Pending.Id != original) throw new PgFailure("database.operation_conflict");
            }
            else
            {
                if (new object?[] { options.FrozenPackage, options.Command, options.AccountCommand, options.AdminRecovery, options.RemoteAccess, options.Maintenance }.Count(x => x is not null) != 1 || options.RecoverOperationId is not null)
                    throw new PgFailure("database.request_invalid");
                var identity = PgHash(JsonSerializer.SerializeToUtf8Bytes(options, PgJson));
                if (ledger.Receipts.FirstOrDefault(x => x.Id == id) is { } receipt)
                {
                    if (receipt.Identity != identity) throw new PgFailure("database.operation_conflict");
                    return PgReply(await PgObserve(ledger, id));
                }
                if (ledger.Pending is { } pending)
                {
                    if (pending.Id != id || pending.Identity != identity) throw new PgFailure("database.recovery_required");
                }
                else
                {
                    PgCheckInstanceMutation(primary, instances, ledger, options);
                    if (ledger.LedgerInstance is not null && instances.All(l => l.DataDeleted))
                    {
                        await PgVerifyConfiguration(primary);
                        await PgExclusiveInstance(primary);
                    }
                    await PgPreflight(ledger, options);
                    if (options.Maintenance is {} maintenance) await PgMaintenancePreflight(ledger, maintenance);
                    if (options.RemoteAccess is { } remote) await PgRemotePreflight(ledger, remote, request.PostgreSqlTls);
                    if (options.AdminRecovery is { } admin) await PgAdminPreflight(ledger, admin);
                    if (options.AccountCommand is { } accountCommand) await PgAccountPreflight(ledger, accountCommand, request.PostgreSqlSecrets);
                    ledger = ledger with { Pending = new(id, identity, options, "Prepared") };
                    PgWrite(ledger);
                }
            }
            if (request.PostgreSqlTls is not null && ledger.Pending?.Request.RemoteAccess is null) throw new PgFailure("database.request_invalid");
            if (request.PostgreSqlSecrets is not null && ledger.Pending?.Request.AccountCommand is null)
                throw new PgFailure("database.request_invalid");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            using var packages = await AcquirePackageResourceLockAsync(timeout.Token);
            ledger = await PgContinue(ledger, request.PostgreSqlSecrets, request.PostgreSqlTls);
            if (request.Operation == PrivilegedOperationKind.PostgreSqlRecover) ledger = PgRecoveryReceipt(ledger, id, options);
            return PgReply(await PgObserve(ledger, options.RecoverOperationId ?? id));
        }
        catch (Exception ex) when (ex is PgFailure or IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            return new(false, 65, Error: ex is PgFailure ? ex.Message : "database.operation_unavailable",
                ProblemCode: PrivilegedProblemCode.RecoveryRequired);
        }
        finally { PgGate.Release(); }
    }

    private static PostgreSqlState PgUnsupported() => new(false, false, false, null, 0, null, 5432, false, false, null, null, "database.platform_unsupported");
    private static PrivilegedOperationResult PgReply(PostgreSqlState state) => new(true,
        OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(state)));
    private static string PgHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static PgLedger PgRecoveryReceipt(PgLedger ledger, Guid id, PostgreSqlInstallationRequest request)
    {
        var identity = PgHash(JsonSerializer.SerializeToUtf8Bytes(request, PgJson));
        if (ledger.Receipts.FirstOrDefault(x => x.Id == id) is { } prior)
        { if (prior.Identity != identity) throw new PgFailure("database.operation_conflict"); return ledger; }
        var next = ledger with { Receipts = [.. ledger.Receipts, new(id, identity)] }; PgWrite(next); return next;
    }
    private static string PgFileHash(string path) { PgNoLinks(path); return PgHash(File.ReadAllBytes(path)); }
    private static void PgNoLinks(string path)
    {
        for (string? p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
            if (new FileInfo(p).LinkTarget is not null || new DirectoryInfo(p).LinkTarget is not null)
                throw new PgFailure("database.path_untrusted");
    }
    private static PgLedger PgRead(Guid? instance = null)
    {
        var path = PgLedgerPath(instance);
        PgNoLinks(path);
        if (!File.Exists(path) && instance is not null) throw new PgFailure("database.state_invalid");
        if (!File.Exists(path)) return new(1, null, null, null, null, 5432, null, null, [], [], []);
        if (new FileInfo(path).Length > 2097152) throw new PgFailure("database.state_invalid");
        LinuxDirectorySync.Flush(PgRoot);
        var value = JsonSerializer.Deserialize<PgLedger>(File.ReadAllBytes(path), PgJson) ?? throw new PgFailure("database.state_invalid");
        if (value.LedgerInstance != instance || value.Databases is null || value.Accounts is null || value.Revision < 1 || value.Receipts is null || value.Receipts.Any(x => x.Id == Guid.Empty || string.IsNullOrEmpty(x.Identity))
            || value.Receipts.Select(x => x.Id).Distinct().Count() != value.Receipts.Count
            || value.Port is < 1024 or > 65535 || value.Instance == Guid.Empty
            || value.Instance is not null && value.Package is null
            || value.Pending is { } pending && (pending.Id == Guid.Empty || string.IsNullOrEmpty(pending.Identity) || pending.Request is null
                || pending.Identity != PgHash(JsonSerializer.SerializeToUtf8Bytes(pending.Request, PgJson))))
            throw new PgFailure("database.state_invalid");
        PgValidateBackupPolicyLedger(value);
        return value;
    }
    private static void PgWrite(PgLedger value)
    {
        if (value.Receipts.Count > 10000) throw new PgFailure("database.state_capacity");
        PgAtomic(PgLedgerPath(value.LedgerInstance), JsonSerializer.SerializeToUtf8Bytes(value, PgJson));
    }
    private static void PgAtomic(string path, byte[] value)
    {
        PgNoLinks(path);
        var temp = path + "." + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            stream.Write(value); stream.Flush(true);
        }
        File.Move(temp, path, true);
        LinuxDirectorySync.Flush(Path.GetDirectoryName(path)!);
    }
    private static PgLedger PgPhase(PgLedger ledger, string phase)
    {
        var next = ledger with { Pending = ledger.Pending! with { Phase = phase } };
        PgWrite(next); return next;
    }

    private static async Task PgPreflight(PgLedger ledger, PostgreSqlInstallationRequest request)
    {
        if (request.FrozenPackage is { } frozen)
        {
            if (frozen.ComponentId != ComponentIds.PostgreSql || frozen.Request.ComponentId != ComponentIds.PostgreSql
                || frozen.Request.Change != RuntimeChange.Install || frozen.Request.Mode != ComponentSourceMode.SystemPackage
                || frozen.Request.HostId != Environment.MachineName || frozen.Request.ExpectedRevision != ledger.Revision
                || frozen.Request.SourceId is not null || frozen.Request.ReleaseId is not null || !frozen.Request.KeepData
                || frozen.Request.ExpectedSourceRevision != 0 || frozen.Request.ExpectedTrustRevision != 0
                || frozen.Request.IdempotencyKey is not { Length: > 0 and <= 128 }
                || frozen.Expected is not { Mode: ComponentSourceMode.SystemPackage, Archive: null, Pin: null, Installer: null, InstalledPackage: null }
                || frozen.Expected?.Package is not { } package || ledger.Package is not null
                || frozen.Request.RequestedVersion is { } requestedVersion && requestedVersion != package.Version)
                throw new PgFailure("database.plan_changed");
            await PgRequireEmptyHost();
            var current = await PgResolve();
            if (!AptPackageResolution.MatchesExpectation(package, current)) throw new PgFailure("database.plan_changed");
        }
        else if (request.Command is { } command)
        {
            if (!Enum.IsDefined(command.Action) || command.InstanceId == Guid.Empty || command.ExpectedRevision != ledger.Revision
                || ledger.Package is null || command.Port is < 1024 or > 65535) throw new PgFailure("database.plan_changed");
            await PgVerifyPackage(ledger);
            if (command.Action == PostgreSqlAction.Initialize)
            {
                if (ledger.Instance is not null) throw new PgFailure("database.instance_exists");
                var plan = PostgreSqlInitializationPlanner.Create(command.InstanceId, command.Port, PgMajor(ledger));
                foreach (var path in new[] { plan.DataDirectory, plan.ConfigurationDirectory, plan.SocketDirectory })
                { PgNoLinks(path); if (Directory.Exists(path) || File.Exists(path)) throw new PgFailure("database.external_installation"); }
                var clusters = await PgRun("/usr/bin/pg_lsclusters", ["--no-header"]);
                if (PostgreSqlInitializationPlanner.CheckClusterPort(clusters.Output, command.Port) is { } problem)
                    throw new PgFailure(problem);
                using var ipv4 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, command.Port);
                ipv4.Start();
                using var ipv6 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.IPv6Loopback, command.Port);
                ipv6.Server.DualMode = false; ipv6.Start();
            }
            else
            {
                if (ledger.Instance != command.InstanceId || ledger.Port != command.Port) throw new PgFailure("database.instance_invalid");
                await PgVerifyConfiguration(ledger);
            }
        }
    }
    private static async Task PgRequireEmptyHost()
    {
        var installed = await PgRun("/usr/bin/dpkg-query", ["-W", "-f=${binary:Package}\t${db:Status-Status}\n", "postgresql*"], allowFailure: true);
        if (installed.Code is not (0 or 1) || installed.Output.Split('\n').Any(x => x.Contains('\t') && !x.EndsWith("not-installed", StringComparison.Ordinal)))
            throw new PgFailure("database.external_installation");
        foreach (var path in new[] { "/etc/postgresql", "/etc/postgresql-common", "/var/lib/postgresql", "/run/postgresql" })
        { PgNoLinks(path); if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()) throw new PgFailure("database.external_installation"); }
        if ((await PgRun("/usr/bin/dpkg", ["--audit"])).Output.Trim().Length != 0) throw new PgFailure("database.package_recovery_required");
    }

    private static string[] PgAptOptions() => ["-o", "Dir::Etc::sourcelist=" + PgRoot + "/sources.list", "-o", "Dir::Etc::sourceparts=-",
        "-o", "Dir::State::lists=" + PgRoot + "/lists", "-o", "Dir::Cache::archives=" + PgRoot + "/archives",
        "-o", "APT::Sandbox::User=root", "-o", "Acquire::AllowInsecureRepositories=false", "-o", "Acquire::AllowDowngradeToInsecureRepositories=false",
        "-o", "APT::Get::AllowUnauthenticated=false", "-o", "Acquire::Check-Valid-Until=true", "-o", "Acquire::ForceHash=SHA256"];
    private static async Task<ComponentPackageTarget> PgResolve()
    {
        // Use only official repositories already present in the host's policy. No global source edits.
        var observed = (await PgRun("/usr/bin/apt-cache", ["policy"])).Output;
        var sources = Regex.Matches(observed, @"(?m)^\s*\d+\s+(https?://(?:archive\.ubuntu\.com|security\.ubuntu\.com|cn\.archive\.ubuntu\.com)/ubuntu/?)[ ]+(noble(?:-updates|-security)?)/main[ ]+amd64[ ]+Packages\s*$")
            .Select(m => $"deb [arch=amd64 signed-by=/usr/share/keyrings/ubuntu-archive-keyring.gpg,F6ECB3762474EDA9D21B7022871920D1991BC93C] {m.Groups[1].Value} {m.Groups[2].Value} main")
            .Distinct().Order().ToArray();
        if (sources.Length == 0) throw new PgFailure("database.source_unsupported");
        PgAtomic(PgRoot + "/sources.list", Encoding.UTF8.GetBytes(string.Join('\n', sources) + "\n"));
        Directory.CreateDirectory(PgRoot + "/lists"); Directory.CreateDirectory(PgRoot + "/archives");
        await PgRun("/usr/bin/apt-get", [.. PgAptOptions(), "update", "-o", "APT::Update::Error-Mode=any"], minutes: 10);
        var policy = (await PgRun("/usr/bin/apt-cache", [.. PgAptOptions(), "policy", PostgreSqlBootstrapPolicy.Package])).Output;
        if (!AptPackageResolution.TryParsePolicy(policy, PostgreSqlBootstrapPolicy.Package, out var parsed) || parsed?.CandidateVersion is not { } version)
            throw new PgFailure("database.package_unresolved");
        var uris = (await PgRun("/usr/bin/apt-get", [.. PgAptOptions(), "-o", "Dir::Cache::archives=" + PgRoot + "/empty",
            "install", "--print-uris", "--reinstall", "--yes", "--no-remove", "--no-install-recommends", PostgreSqlBootstrapPolicy.Package + "=" + version])).Output;
        if (!PostgreSqlBootstrapPolicy.TryResolve(policy, uris, out var target, out var problem) || target is null)
            throw new PgFailure(problem ?? "database.package_unresolved");
        return new(target.Package, target.Version, target.Architecture, target.RepositoryId, target.BaseUri, target.Suite, target.Area,
            target.PoolUri, target.Sha256, target.Bytes, target.Dependencies.Select(x => new ComponentPackageArtifact(x.FileName, x.Uri, x.Bytes, x.Sha256)).ToArray());
    }

    private static async Task<PgLedger> PgContinue(PgLedger ledger, PostgreSqlSecrets? secrets = null, PostgreSqlTlsMaterial? tls = null)
    {
        var pending = ledger.Pending ?? throw new PgFailure("database.operation_unknown");
        if (pending.Request.Maintenance is null && pending.Request.RemoteAccess is null && pending.Request.AdminRecovery is null && pending.Request.AccountCommand is null && pending.Phase.EndsWith("Dispatched", StringComparison.Ordinal)) throw new PgFailure("database.recovery_required");
        if (pending.Request.FrozenPackage?.Expected?.Package is { } package)
        {
            if (ledger.Pending!.Phase == "Prepared")
            {
                await PgRequireEmptyHost();
                if (!AptPackageResolution.MatchesExpectation(package, await PgResolve())) throw new PgFailure("database.plan_changed");
                var names = package.AlsoInstalls.Select(x => Uri.UnescapeDataString(x.FileName).Split('_'))
                    .Select(x => x[0] + "=" + x[1]).Prepend(package.PackageName + "=" + package.Version).ToArray();
                await PgRun("/usr/bin/apt-get", [.. PgAptOptions(), "install", "--download-only", "--yes", "--no-remove", "--no-install-recommends", .. names], minutes: 15);
                foreach (var file in package.AlsoInstalls.Prepend(new(Path.GetFileName(new Uri(package.PoolUri).AbsolutePath), package.PoolUri, package.Bytes, package.Sha256)))
                {
                    var path = PgRoot + "/archives/" + Uri.UnescapeDataString(file.FileName).Replace(":", "%3a", StringComparison.Ordinal);
                    if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes || !PgFileHash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new PgFailure("database.artifact_changed");
                }
                ledger = PgPhase(ledger, "Downloaded");
            }
            if (ledger.Pending!.Phase == "Downloaded")
            {
                PgNoLinks(PgCreateConfig);
                Directory.CreateDirectory(Path.GetDirectoryName(PgCreateConfig)!);
                if (File.Exists(PgCreateConfig) && File.ReadAllText(PgCreateConfig) != PgSuppression) throw new PgFailure("database.configuration_drift");
                PgAtomic(PgCreateConfig, Encoding.UTF8.GetBytes(PgSuppression));
                ledger = PgPhase(ledger, "Suppressed");
            }
            if (ledger.Pending!.Phase == "Suppressed")
            {
                if (File.ReadAllText(PgCreateConfig) != PgSuppression) throw new PgFailure("database.configuration_drift");
                ledger = PgPhase(ledger, "PackageDispatched");
                var names = package.AlsoInstalls.Select(x => Uri.UnescapeDataString(x.FileName).Split('_')).Select(x => x[0] + "=" + x[1]);
                await PgRun("/usr/bin/apt-get", [.. PgAptOptions(), "install", "--no-download", "--yes", "--no-remove", "--no-install-recommends",
                    "-o", "Dpkg::Options::=--force-confold", package.PackageName + "=" + package.Version, .. names], minutes: 15);
                ledger = ledger with { Package = package, BinaryHash = PgFileHash(PostgreSqlBootstrapPolicy.Executable), ServiceHash = PgFileHash(PostgreSqlBootstrapPolicy.ServiceTemplate) };
                await PgVerifyPackage(ledger);
                if ((await PgRun("/usr/bin/pg_lsclusters", ["--no-header"])).Output.Trim().Length != 0)
                    throw new PgFailure("database.external_installation");
                ledger = PgPhase(ledger, "PackagesVerified");
            }
            if (ledger.Pending!.Phase == "PackagesVerified")
            {
                // Deletion only removes the exact temporary content owned by this transaction.
                PgNoLinks(PgCreateConfig);
                if (File.Exists(PgCreateConfig))
                {
                    if (File.ReadAllText(PgCreateConfig) != PgSuppression) throw new PgFailure("database.configuration_drift");
                    File.Delete(PgCreateConfig); LinuxDirectorySync.Flush(Path.GetDirectoryName(PgCreateConfig)!);
                }
                ledger = PgPhase(ledger, "Verified");
            }
        }
        else if (pending.Request.Command is { } command)
        {
            await PgVerifyPackage(ledger);
            var plan = PostgreSqlInitializationPlanner.Create(command.InstanceId, command.Port, PgMajor(ledger));
            if (command.Action == PostgreSqlAction.Initialize)
            {
                if (ledger.Pending!.Phase == "Prepared")
                {
                    var parent = Path.GetDirectoryName(plan.DataDirectory)!;
                    PgNoLinks(parent);
                    await PgRun("/usr/bin/install", ["-d", "-m", "700", "-o", "postgres", "-g", "postgres", parent]);
                    ledger = PgPhase(ledger, "InitializeDispatched");
                    await PgRun("/usr/bin/pg_createcluster", [.. plan.CreateArguments], minutes: 3);
                    ledger = PgPhase(ledger, "Initialized");
                }
                if (ledger.Pending!.Phase == "Initialized")
                {
                    PgAtomic(plan.ConfigurationDirectory + "/pg_hba.conf", Encoding.UTF8.GetBytes(plan.HbaConfiguration));
                    await PgRun("/usr/bin/chown", ["postgres:postgres", plan.ConfigurationDirectory + "/pg_hba.conf"]);
                    ledger = ledger with { Instance = command.InstanceId, Port = command.Port, ConfigurationHash = PgConfigurationHash(plan) };
                    ledger = PgPhase(ledger, "Configured");
                }
                if (ledger.Pending!.Phase == "Configured")
                {
                    ledger = PgPhase(ledger, "StartDispatched");
                    await PgRun("/usr/bin/systemctl", ["start", plan.ServiceName]);
                    ledger = PgPhase(ledger, "Started");
                }
            }
            else if (ledger.Pending!.Phase == "Prepared")
            {
                await PgVerifyConfiguration(ledger);
                if (command.Action is PostgreSqlAction.Start or PostgreSqlAction.Stop)
                {
                    ledger = PgPhase(ledger, command.Action + "Dispatched");
                    await PgRun("/usr/bin/systemctl", [command.Action == PostgreSqlAction.Start ? "start" : "stop", plan.ServiceName]);
                }
                ledger = PgPhase(ledger, command.Action == PostgreSqlAction.Stop ? "Stopped" : "Started");
            }
            if (ledger.Pending!.Phase == "Started")
            {
                await PgVerifyConnection(ledger);
                ledger = PgPhase(ledger, "Verified");
            }
            if (ledger.Pending!.Phase == "Stopped")
            {
                if ((await PgRun("/usr/bin/systemctl", ["show", "-p", "ActiveState", "-p", "MainPID", plan.ServiceName])).Output.Trim()
                    .Split('\n').Order().SequenceEqual(new[] { "ActiveState=inactive", "MainPID=0" }) != true)
                    throw new PgFailure("database.stop_unconfirmed");
                ledger = PgPhase(ledger, "Verified");
            }
        }
        else if (pending.Request.Maintenance is { Action: PostgreSqlMaintenanceAction.DeleteInstance }) ledger = await PgContinueInstanceDeletion(ledger);
        else if (pending.Request.Maintenance is {Action:PostgreSqlMaintenanceAction.MigrateMajor}) ledger=await PgContinueMajor(ledger);
        else if (pending.Request.Maintenance is {Action:PostgreSqlMaintenanceAction.ConfigureBackupSchedule or PostgreSqlMaintenanceAction.ScheduledBackup or PostgreSqlMaintenanceAction.DeleteBackup}) ledger=await PgContinueBackupPolicy(ledger);
        else if (pending.Request.Maintenance is not null) ledger = await PgContinueMaintenance(ledger);
        else if (pending.Request.RemoteAccess is not null) ledger = await PgContinueRemote(ledger, tls);
        else if (pending.Request.AdminRecovery is not null) ledger = await PgContinueAdminRecovery(ledger);
        else if (pending.Request.AccountCommand is { } accountCommand) ledger = await PgContinueAccount(ledger, accountCommand, secrets);
        if (ledger.Pending!.Phase != "Verified") throw new PgFailure("database.state_invalid");
        if(pending.Request.Maintenance?.Action is PostgreSqlMaintenanceAction.Update or PostgreSqlMaintenanceAction.UninstallKeepData or PostgreSqlMaintenanceAction.UninstallDeleteData)
            ledger=ledger with {BackupSchedule=pending.Request.Maintenance.Action==PostgreSqlMaintenanceAction.UninstallDeleteData?null:ledger.BackupSchedule is {} policy?policy with {Enabled=false,LastOutcome="database.backup_schedule_lifecycle"}:null};
        var completed = ledger with { Revision = ledger.Revision + 1, Pending = null, Receipts = [.. ledger.Receipts, new(pending.Id, pending.Identity)] };
        PgWrite(completed);
        return completed;
    }

    private static async Task PgVerifyPackage(PgLedger ledger)
    {
        if (ledger.Package is null || PgFileHash(PgBin(ledger, "postgres")) != ledger.BinaryHash
            || PgFileHash(PostgreSqlBootstrapPolicy.ServiceTemplate) != ledger.ServiceHash) throw new PgFailure("database.package_identity_changed");
        var version = (await PgRun("/usr/bin/dpkg-query", ["-W", "-f=${Status}|${Version}|${Architecture}", ledger.Package!.PackageName])).Output.Trim();
        if (version != "install ok installed|" + ledger.Package.Version + "|amd64") throw new PgFailure("database.package_identity_changed");
        foreach (var pair in new[] { (PgBin(ledger, "postgres"), ledger.Package!.PackageName), (PostgreSqlBootstrapPolicy.ServiceTemplate, "postgresql-common") })
        {
            var owner = (await PgRun("/usr/bin/dpkg-query", ["-S", pair.Item1])).Output.Trim();
            if (owner != pair.Item2 + ": " + pair.Item1) throw new PgFailure("database.package_identity_changed");
        }
        if ((await PgRun("/usr/bin/dpkg", ["--audit"])).Output.Trim().Length != 0) throw new PgFailure("database.package_recovery_required");
        var integrity = await PgRun("/usr/bin/dpkg", ["--verify", ledger.Package!.PackageName, "postgresql-client-"+PgMajor(ledger), "postgresql-common", "postgresql-client-common"], allowFailure: true);
        if (integrity.Code is not (0 or 1) || integrity.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => !line.EndsWith(" " + PgCreateConfig, StringComparison.Ordinal)
                && !(line.StartsWith("missing", StringComparison.Ordinal) && (line.Contains(" /usr/share/doc/", StringComparison.Ordinal) || line.Contains(" /usr/share/man/", StringComparison.Ordinal) || line.Contains(" /usr/share/locale/", StringComparison.Ordinal) && line.EndsWith(".mo", StringComparison.Ordinal))))) throw new PgFailure("database.package_identity_changed");
        foreach (var unit in new[] { "/etc/systemd/system/postgresql@.service", "/run/systemd/system/postgresql@.service" })
            if (File.Exists(unit) || Directory.Exists(unit + ".d")) throw new PgFailure("database.service_identity_changed");
    }
    private static string PgConfigurationHash(PostgreSqlInitialization plan)
    {
        PgNoLinks(plan.DataDirectory); PgNoLinks(plan.SocketDirectory);
        var includes = plan.ConfigurationDirectory + "/conf.d";
        PgNoLinks(includes);
        if (Directory.Exists(includes) && Directory.EnumerateFileSystemEntries(includes).Any()) throw new PgFailure("database.configuration_drift");
        var hashes = new[] { "postgresql.conf", "pg_hba.conf", "pg_ident.conf", "start.conf", "pg_ctl.conf" }
            .Select(x => PgFileHash(plan.ConfigurationDirectory + "/" + x));
        return PgHash(Encoding.UTF8.GetBytes(string.Join('|', hashes.Append(PgFileHash(plan.DataDirectory + "/postgresql.auto.conf")).Concat(new[] { "rk-server.pem", "rk-server.key" }.Where(x => File.Exists(plan.ConfigurationDirectory + "/" + x)).Select(x => PgFileHash(plan.ConfigurationDirectory + "/" + x))))));
    }
    private static Task PgVerifyConfiguration(PgLedger ledger, bool allowAdminMask = false)
    {
        var plan = PostgreSqlInitializationPlanner.Create(ledger.Instance!.Value, ledger.Port, PgMajor(ledger));
        if (PgConfigurationHash(plan) != ledger.ConfigurationHash) throw new PgFailure("database.configuration_drift");
        foreach (var root in new[] { "/etc/systemd/system/", "/run/systemd/system/", "/usr/lib/systemd/system/" })
            if ((File.Exists(root + plan.ServiceName) || new FileInfo(root + plan.ServiceName).LinkTarget is not null) && !(allowAdminMask && root == "/etc/systemd/system/" && PgOwnMask(plan)) || Directory.Exists(root + plan.ServiceName + ".d") && !(root == "/etc/systemd/system/" && PgRemoteOwnDropIn(ledger))) throw new PgFailure("database.service_identity_changed");
        return Task.CompletedTask;
    }
    private static async Task<bool> PgRunning(PostgreSqlInitialization plan) =>
        (await PgRun("/usr/bin/systemctl", ["is-active", plan.ServiceName], allowFailure: true)).Output.Trim() == "active";
    private static async Task PgVerifyConnection(PgLedger ledger)
    {
        await PgVerifyConfiguration(ledger);
        await PgRemoteVerify(ledger);
        var plan = PostgreSqlInitializationPlanner.Create(ledger.Instance!.Value, ledger.Port, PgMajor(ledger));
        if (!await PgRunning(plan)) throw new PgFailure("database.not_running");
        var unit = (await PgRun("/usr/bin/systemctl", ["show", "-p", "FragmentPath", "-p", "DropInPaths", plan.ServiceName])).Output.Trim().Split('\n').Order().ToArray();
        if (!unit.SequenceEqual(new[] { "DropInPaths=" + (PgRemoteOwnDropIn(ledger) ? PgRemoteDropIn(ledger) + "/50-rk-database.conf" : ""), "FragmentPath=" + PostgreSqlBootstrapPolicy.ServiceTemplate }))
            throw new PgFailure("database.service_identity_changed");
        var pidText = (await PgRun("/usr/bin/systemctl", ["show", "-p", "MainPID", "--value", plan.ServiceName])).Output.Trim();
        if (!int.TryParse(pidText, out var pid) || pid <= 1 || File.ResolveLinkTarget($"/proc/{pid}/exe", true)?.FullName != PgBin(ledger, "postgres"))
            throw new PgFailure("database.service_identity_changed");
        const string query = "SELECT current_user, current_setting('data_directory'), current_setting('port'), current_setting('server_version_num'), (SELECT count(*) FROM pg_hba_file_rules WHERE error IS NOT NULL);";
        var output = (await PgRun("/usr/sbin/runuser", ["-u", "postgres", "--", PgBin(ledger, "psql"), "-XAtw", "-v", "ON_ERROR_STOP=1",
            "-h", plan.SocketDirectory, "-p", ledger.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-d", "postgres"], input: query)).Output.Trim().Split('|');
        if (output.Length != 5 || output[0] != "postgres" || output[1] != plan.DataDirectory || output[2] != ledger.Port.ToString()
            || !int.TryParse(output[3], out var nativeVersion) || nativeVersion / 10000 != PgMajor(ledger) || output[4] != "0")
            throw new PgFailure("database.connection_unverified");
        if ((await PgRun("/usr/bin/systemctl", ["show", "-p", "MainPID", "--value", plan.ServiceName])).Output.Trim() != pidText)
            throw new PgFailure("database.service_identity_changed");
    }
    private static async Task<PostgreSqlState> PgObserve(PgLedger ledger, Guid operation)
    {
        string? problem = null; ComponentPackageTarget? candidate = null; bool running = false, verified = false;
        try
        {
            if (ledger.LedgerInstance is not null && (ledger.DataDeleted ||
                ledger.Pending?.Request.Maintenance?.Action == PostgreSqlMaintenanceAction.DeleteInstance))
            {
                // Deletion receipts and pending work must remain readable after any owned path is gone.
            }
            else if (ledger.PackageRemoved) { problem = "database.package_retained"; }
            else if (ledger.Package is null)
            { if (ledger.Pending is null) { await PgRequireEmptyHost(); candidate = await PgResolve(); } }
            else
            {
                await PgVerifyPackage(ledger);
                if (ledger.Instance is { } instance)
                {
                    await PgVerifyConfiguration(ledger, allowAdminMask: ledger.Pending?.Request.AdminRecovery is not null || ledger.Pending?.Request.Maintenance is not null);
                    running = await PgRunning(PostgreSqlInitializationPlanner.Create(instance, ledger.Port, PgMajor(ledger)));
                    if (running)
                    {
                        await PgVerifyConnection(ledger); verified = true;
                        if (ledger.Pending is null)
                        {
                            foreach (var database in ledger.Databases) await PgVerifyDatabase(ledger, database);
                            foreach (var account in ledger.Accounts) await PgVerifyAccount(ledger, account);
                        }
                    }
                }
            }
        }
        catch (PgFailure ex) { problem = ex.Message; }
        return new(true, ledger.Package is not null && !ledger.PackageRemoved, ledger.Package is not null, ledger.Package?.Version,
            ledger.Revision, ledger.Instance, ledger.Port, running, verified, ledger.Pending?.Id,
            ledger.Receipts.Any(x => x.Id == operation) ? operation : null, ledger.Pending is null ? problem : "database.recovery_required", candidate, ledger.Databases, ledger.Accounts, ledger.Pending?.Request.AccountCommand, ledger.Pending?.Request.AdminRecovery, ledger.RemoteAccess, ledger.Pending?.Request.RemoteAccess, ledger.Pending?.Request.Maintenance, ledger.Backups, ledger.PackageRemoved, ledger.DataDeleted, ledger.BackupSchedule);
    }
    private static async Task<PgCommandResult> PgRun(string executable, string[] arguments, bool allowFailure = false, int minutes = 1, string? input = null, string? passfile = null, string? workingDirectory = null)
    {
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        if (workingDirectory is not null) process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.Environment.Clear();
        process.StartInfo.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        process.StartInfo.Environment["LANG"] = "C.UTF-8";
        process.StartInfo.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        if (passfile is not null) process.StartInfo.Environment["PGPASSFILE"] = passfile;
        process.StartInfo.Environment["PGCONNECT_TIMEOUT"] = "5";
        foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) throw new PgFailure("database.native_failed");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(minutes));
        async Task<string> ReadBounded(StreamReader reader)
        {
            var text = new StringBuilder(); var buffer = new char[4096]; int count;
            while ((count = await reader.ReadAsync(buffer, deadline.Token)) != 0)
            { if (text.Length + count > 262144) throw new PgFailure("database.native_output_limit"); text.Append(buffer, 0, count); }
            return text.ToString();
        }
        var stdout = ReadBounded(process.StandardOutput); var stderr = ReadBounded(process.StandardError);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
            if (process.ExitCode != 0 && !allowFailure) throw new PgFailure("database.native_failed");
            return new(process.ExitCode, await stdout);
        }
        catch { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
    }
}

