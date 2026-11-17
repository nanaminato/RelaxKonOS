using System.Text.Json;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.ManagedRuntimes.PostgreSql;

if(args is ["scheduler-native"]){await PostgreSqlSchedulerSmoke.RunAsync();return;}

if (args is ["helper-json", _] or ["helper-stdin"])
{
    var request = JsonSerializer.Deserialize<RelaxKonOS.Protocol.Privileged.PrivilegedOperationRequest>((args.Length == 1 ? await Console.In.ReadToEndAsync() : File.ReadAllText(args[1])))!;
    var id = request.OperationId ?? Guid.NewGuid();
    request = request with { OperationId = id, Correlation = RelaxKonOS.Protocol.Observability.CorrelationContext.Create(id, "privileged.operation") };
    var result = await PrivilegedOperationExecutor.ExecuteAsync(request, new([], [], [], []));
    Console.WriteLine(JsonSerializer.Serialize(result));
    return;
}

var instance = Guid.Parse("c4207665-91c4-47e9-8978-6639a70b5de0");
if (args is ["initialization-json"])
{
    Console.WriteLine(JsonSerializer.Serialize(PostgreSqlInitializationPlanner.Create(instance, 55432, 16)));
    return;
}

var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    checks++;
    Console.WriteLine("PASS " + label);
}
var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
var policy = File.ReadAllText(Path.Combine(fixtures, "noble-policy.txt"));
var artifacts = File.ReadAllText(Path.Combine(fixtures, "noble-install-uris.txt"));
Check(PostgreSqlBootstrapPolicy.TryResolve(policy, artifacts, out var target, out var problem)
    && target is not null && problem is null, "actual Noble APT snapshot resolves");
Check(target!.Version == "16.15-0ubuntu0.24.04.1"
    && target.Sha256 == "d60ae76fb862cd4cca7bfdcdcdcc271c5e1d75f6b7915305ce2d962735a4b666"
    && target.Dependencies.Count == 36, "exact server artifact and full dependency closure retained");
Check(target.Dependencies.Any(x => x.FileName.StartsWith("libc6_", StringComparison.Ordinal)), "system package upgrades remain visible");
Check(!PostgreSqlBootstrapPolicy.TryResolve(policy.Replace("Installed: (none)", "Installed: 16.2-1ubuntu4"), artifacts, out _, out problem)
    && problem == "database.external_installation", "external install is never adopted by a fresh plan");
foreach (var version in new[] { "16.2-1ubuntu4", "16.14-0ubuntu0.24.04.1", "17.1-0ubuntu0.24.04.1", "16.15-1.pgdg24.04+1", "16.15-0ubuntu0.26.04.1", "16.15rc1", "16.15-0ubuntu0.24.04.1\n" })
    Check(!PostgreSqlBootstrapPolicy.SupportsPackageVersion(version), "reject unsupported version " + version.Trim());
Check(PostgreSqlBootstrapPolicy.SupportsPackageVersion("16.16-0ubuntu0.24.04.1"), "new stable Noble patch can be evaluated without a hardcoded release list");
Check(PostgreSqlBootstrapPolicy.CheckTarget(target with { Suite = "resolute-updates" }) == "database.source_unsupported", "cross-release repository refused");
Check(PostgreSqlBootstrapPolicy.CheckTarget(target with { Architecture = "arm64" }) == "database.platform_unsupported", "unverified architecture refused");
Check(PostgreSqlBootstrapPolicy.CheckTarget(target with { Sha256 = "" }) == "database.package_unresolved", "missing SHA256 refused");
foreach (var uri in new[] { "https://archive.ubuntu.com.evil.example/ubuntu/pool/x.deb", "http://archive.ubuntu.com:8080/ubuntu/pool/x.deb", "http://archive.ubuntu.com/ubuntu/pool/x.deb?token=x" })
    Check(PostgreSqlBootstrapPolicy.CheckTarget(target with { Dependencies = target.Dependencies.Select((x, i) => i == 0 ? x with { Uri = uri } : x).ToArray() })
        is not null, "dependency origin cannot widen source policy");
Check(PostgreSqlBootstrapPolicy.CheckTarget(target with { Dependencies = target.Dependencies.Where(x => !x.FileName.StartsWith("postgresql-common_", StringComparison.Ordinal)).ToArray() })
    == "database.package_dependencies_incomplete", "service-owner package required separately from server binary");
Check(!AptPackageResolution.MatchesModuleResolution(target, target with { Dependencies = target.Dependencies.Skip(1).ToArray() }), "frozen dependency removal is drift");
Check(!PostgreSqlBootstrapPolicy.TryResolve(policy.Replace("  Installed: (none)", ""), artifacts, out _, out problem)
    && problem == "database.inventory_unknown", "missing installed state cannot imply absence");
Check(!PostgreSqlBootstrapPolicy.TryResolve(policy.Replace("Installed: (none)", "Installed: unknown"), artifacts, out _, out problem)
    && problem == "database.inventory_unknown", "unknown installed state cannot imply absence");
Check(PostgreSqlBootstrapPolicy.CheckTarget(target with { RepositoryId = "forged|noble|main|amd64" }) == "database.source_unsupported", "repository identity matches its fields");
var common = target.Dependencies.Single(x => x.FileName.StartsWith("postgresql-common_", StringComparison.Ordinal));
Check(PostgreSqlBootstrapPolicy.CheckTarget(target with { Dependencies = [.. target.Dependencies, common with { FileName = "postgresql-common_257build1.2_all.deb" }] })
    == "database.package_dependencies_unsupported", "two revisions of the same dependency cannot be frozen");
var host = new PostgreSqlHostObservation("linux", "ubuntu", "24.04", "amd64", true, true, [], [], true, true);
Check(PostgreSqlBootstrapPolicy.CheckHost(host) is null, "clean target passes preflight, without granting execution");
Check(PostgreSqlBootstrapPolicy.CheckHost(host with { Version = "26.04" }) == "database.platform_unsupported", "actual 26.04 acceptance host cannot stand in for Noble");
Check(PostgreSqlBootstrapPolicy.CheckHost(host with { SystemdAvailable = false }) == "database.service_host_unavailable", "package-test container cannot claim systemd readiness");
Check(PostgreSqlBootstrapPolicy.CheckHost(host with { ExistingPackages = null }) == "database.inventory_unknown", "unknown inventory is not absent");
Check(PostgreSqlBootstrapPolicy.CheckHost(host with { ExistingPackages = ["postgresql-common"] }) == "database.external_installation", "shared package collision blocks bootstrap");
Check(PostgreSqlBootstrapPolicy.CheckHost(host with { PathsAbsent = false }) == "database.external_installation", "nonempty or foreign paths block bootstrap");
Check(PostgreSqlBootstrapPolicy.CheckHost(host with { PortAvailable = false }) == "database.port_conflict", "port collision blocks bootstrap");
Check(PostgreSqlBootstrapPolicy.CheckHost(host with { PackageAuditClean = false }) == "database.package_recovery_required", "pending package state blocks bootstrap");
var init = PostgreSqlInitializationPlanner.Create(instance, 55432, 16);
Check(init.CreateArguments.Contains("--auth-local=peer") && init.CreateArguments.Contains("--auth-host=scram-sha-256")
    && !init.CreateArguments.Contains("--start"), "both authentication classes explicit and no start before validation");
Check(!init.HbaConfiguration.Contains("trust", StringComparison.Ordinal) && init.HbaConfiguration.Contains("::/0 reject", StringComparison.Ordinal), "HBA denies other sources in both families");
Check(init.ServiceName == "postgresql@16-rkc420766591c447e989786639a70b5de0.service"
    && init.SocketDirectory != "/tmp" && init.DataDirectory.Contains(instance.ToString("N")), "stable instance-bound paths and concrete service identity");
var nextMajor = PostgreSqlInitializationPlanner.Create(instance, 55432, 18);
Check(nextMajor.ServiceName.StartsWith("postgresql@18-", StringComparison.Ordinal) && nextMajor.ConfigurationDirectory.StartsWith("/etc/postgresql/18/", StringComparison.Ordinal)
    && nextMajor.DataDirectory == init.DataDirectory && nextMajor.CreateArguments.Contains("18"), "new major preserves instance data identity but changes service and configuration identity");
try { PostgreSqlInitializationPlanner.Create(instance, 55432, 19); throw new Exception("accepted unsupported major"); }
catch (ArgumentOutOfRangeException) { checks++; }
var secondInstance = Guid.Parse("11abdb5e-2aff-4d11-8fc8-2cf21c441233");
var second = PostgreSqlInitializationPlanner.Create(secondInstance, 55433, 16);
Check(second.DataDirectory != init.DataDirectory && second.ConfigurationDirectory != init.ConfigurationDirectory
    && second.SocketDirectory != init.SocketDirectory && second.ServiceName != init.ServiceName,
    "same-major clusters use distinct data, configuration, socket and service identities");
Check(PostgreSqlInitializationPlanner.BackupRoot(instance) != PostgreSqlInitializationPlanner.BackupRoot(secondInstance),
    "backup roots differ even when backup IDs match");
const string stoppedCluster = "16 external 55432 down postgres /var/lib/postgresql/16/external /var/log/postgresql/pg.log";
Check(PostgreSqlInitializationPlanner.CheckClusterPort(stoppedCluster, 55432) == "database.port_conflict",
    "stopped cluster retains its configured port reservation");
Check(PostgreSqlInitializationPlanner.CheckClusterPort(stoppedCluster, 55433) is null,
    "distinct configured ports remain eligible for the native bind probe");
Check(PostgreSqlInitializationPlanner.CheckClusterPort("16 external unknown down", 55433) == "database.inventory_unknown",
    "unparseable cluster inventory blocks port assignment");
foreach (var port in new[] { -1, 0, 1023, 65536 })
{
    try { PostgreSqlInitializationPlanner.Create(instance, port, 16); throw new Exception("accepted invalid port"); }
    catch (ArgumentOutOfRangeException) { checks++; }
}
try { PostgreSqlInitializationPlanner.Create(Guid.Empty, 5432, 16); throw new Exception("accepted empty instance"); }
catch (ArgumentException) { checks++; }
Console.WriteLine($"PostgreSQL checks passed: {checks}");
