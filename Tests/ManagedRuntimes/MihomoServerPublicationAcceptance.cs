using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Startup.Modules;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy.Platform;

/// <summary>Real Server authority/coordinator through the in-process Helper boundary to the fixed real
/// systemd service. This does not exercise installed Helper IPC, HTTP routes or application DI.</summary>
internal static class MihomoServerPublicationAcceptance
{
    internal static async Task VerifyNativeValidatorAsync(string binary)
    {
        MihomoIpcStartupAcceptance.RequirePrivateHost();
        var fixture = Directory.CreateTempSubdirectory("mihomo-validator-");
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var ct = budget.Token;
            var paths = new Paths(fixture.FullName, "");
            var official = new MihomoOfficialRuntimeState(paths);
            var validator = new MihomoConfigurationValidator(paths, official, new ProxyDiagnosticLogStore(paths));
            const string version = "v1.19.33";
            Directory.CreateDirectory(official.VersionDirectory(version));
            Directory.CreateDirectory(paths.GetEngineDataDirectory(MihomoEngine.Id));
            Directory.CreateDirectory(paths.GetStateDirectory());
            var pointer = System.Text.Json.JsonSerializer.Serialize(new MihomoOfficialRuntimeState.RuntimeState(version, null, DateTimeOffset.UtcNow));
            await File.WriteAllTextAsync(official.PointerPath, pointer, ct);
            var executable = official.ExecutablePath(version);
            File.Copy(binary, executable);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var configuration = Path.Combine(fixture.FullName, "candidate.yaml");
            await File.WriteAllTextAsync(configuration, "mixed-port: 7890\nmode: rule\nrules:\n  - MATCH,DIRECT\n", ct);
            if (await validator.ValidateAsync(configuration, ct) is not null)
                throw new InvalidOperationException("The scoped validator did not run the real Mihomo configuration checker.");
            await File.WriteAllTextAsync(configuration, "", ct);
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nprintf '%s' \"$$\" > \"$5\"\nexec /usr/bin/sleep 30\n", ct);
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var validation = validator.ValidateAsync(configuration, cancelled.Token);
            int pid;
            while (!int.TryParse(await File.ReadAllTextAsync(configuration, ct), out pid))
                await Task.Delay(15, ct);
            var manager = new MihomoRuntimeStatusReader(null!, official);
            var observation = manager.GetObservedAsync(_ => Task.FromResult(new ControllerResult<bool>(true, "")), ct);
            if (observation.IsCompleted || official.Gate.CurrentCount != 0 || !Directory.Exists($"/proc/{pid}"))
                throw new InvalidOperationException("Runtime observation escaped the original validation gate.");
            await cancelled.CancelAsync();
            try { await validation; throw new InvalidOperationException("The native validation was not cancelled."); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            if ((await observation).Runtime.State != ProxyRuntimeState.Running || Directory.Exists($"/proc/{pid}")
                || await File.ReadAllTextAsync(official.PointerPath, ct) != pointer)
                throw new InvalidOperationException("The original gate was released before the validation process exited or changed the official pointer.");
            Console.WriteLine($"PASS MIHOMO SCOPED NATIVE VALIDATOR: parsed real candidate; PID={pid} exited before official observation resumed");
            await VerifyOfficialVersionProbeAsync(binary, fixture.FullName, paths, official, pointer);
        }
        finally { fixture.Delete(recursive: true); }
    }

    private static async Task VerifyOfficialVersionProbeAsync(string binary, string root, Paths paths,
        MihomoOfficialRuntimeState official, string pointer)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = budget.Token;
        var marker = Path.Combine(root, "version-probe.pid");
        var executable = Path.Combine(root, "version-probe");
        await File.WriteAllTextAsync(executable, $"""
            #!/bin/sh
            set -e
            test "$1" = "-v"
            /usr/bin/head -c 131072 /dev/zero
            /usr/bin/head -c 131072 /dev/zero >&2
            printf '%s' "$$" > '{marker}'
            exec /usr/bin/sleep 30
            """.ReplaceLineEndings("\n"), ct);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var archive = Path.Combine(root, "candidate.gz");
        await using (var output = File.Create(archive))
        await using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        await using (var input = File.OpenRead(binary))
            await input.CopyToAsync(gzip, ct);
        using var source = new InstallationFileSource(File.OpenRead(archive), "candidate.gz", new FileInfo(archive).Length, null);
        var progress = new ProbeInstallProgress();
        var probe = new MihomoRuntimeProbe();
        var installer = new MihomoRuntimeManager(paths, null!, null!, new BlockingOfficialVersionProbe(probe, executable), null!,
            paths, new(), official);
        const string version = "v1.19.34";
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var install = installer.InstallOfficialAsync(version, source, progress, cancel.Token);
        var pid = await WaitForProbeAsync(install);
        var observer = new MihomoRuntimeStatusReader(null!, official);
        var observation = observer.GetObservedAsync(_ =>
        {
            if (Directory.Exists($"/proc/{pid}"))
                throw new InvalidOperationException("Official status resumed before the original version process exited.");
            return Task.FromResult(new ControllerResult<bool>(true, ""));
        }, ct);
        if (observation.IsCompleted || !Directory.Exists($"/proc/{pid}"))
            throw new InvalidOperationException("The official install did not hold the original version gate during probing.");
        await cancel.CancelAsync();
        try { await install.WaitAsync(ct); throw new InvalidOperationException("The version probe swallowed caller cancellation."); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested && !ct.IsCancellationRequested) { }
        if ((await observation).Runtime.State != ProxyRuntimeState.Running
            || await File.ReadAllTextAsync(official.PointerPath, ct) != pointer
            || !File.Exists(official.InstallationOperationPath(progress.OperationId))
            || Directory.Exists(official.VersionDirectory(version))
            || Directory.EnumerateDirectories(paths.GetEngineVersionsDirectory(MihomoEngine.Id), ".staging-*").Any())
            throw new InvalidOperationException("The cancelled original install changed the active version or retained staging.");

        File.Delete(marker);
        var timeout = probe.GetVersionAsync(executable, ct);
        var timedOutPid = await WaitForProbeAsync(timeout);
        if (await timeout is not null || Directory.Exists($"/proc/{timedOutPid}"))
            throw new InvalidOperationException("The internal timeout returned before its original version process exited.");
        Console.WriteLine($"PASS MIHOMO OFFICIAL VERSION PROBE DRAIN: original={progress.OperationId}; cancelled PID={pid}; timeout PID={timedOutPid}; both streams pumped and original gate retained");

        async Task<int> WaitForProbeAsync(Task execution)
        {
            while (true)
            {
                if (File.Exists(marker) && int.TryParse(await File.ReadAllTextAsync(marker, ct), out var started))
                    return started;
                if (execution.IsCompleted)
                {
                    await execution;
                    throw new InvalidOperationException("The version probe finished before reaching the fixture barrier.");
                }
                await Task.Delay(15, ct);
            }
        }
    }

    private sealed class ProbeInstallProgress : IInstallationProgress
    {
        public Guid OperationId { get; } = Guid.NewGuid();
        public Task ReportAsync(InstallationProgress progress, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class BlockingOfficialVersionProbe(MihomoRuntimeProbe probe, string executable) : IMihomoRuntimeProbe
    {
        public async Task<string?> GetVersionAsync(string executablePath, CancellationToken cancellationToken)
        {
            if (await probe.GetVersionAsync(executablePath, cancellationToken) is not { } version
                || !version.Contains("Mihomo", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The reviewed staged Mihomo binary did not report its version.");
            return await probe.GetVersionAsync(executable, cancellationToken);
        }
    }

    internal static async Task<int> RunAsync(MihomoRuntimeLifecycle lifecycle, MihomoPublishedRuntime original,
        int originalPort, Func<int, Task<bool>> probe, Func<int, Func<Task>, Task<bool>> probeWithConnection)
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        var root = Path.Combine(Path.GetTempPath(), "mihomo-server-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new Paths(root, original.ControllerSecret);
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
        var store = new MihomoPublicationStore(paths, protection);
        var transport = new Transport(lifecycle);
        var publicationProfiles = await MihomoProfilePublicationChecks.CreateAsync(root);
        var hostFailures = 0;
        var systemProxy = MihomoPublicationTransactionChecks.Stub<IHostSystemProxyService>(method => method.Name switch
        {
            "ApplyAsync" => Task.FromResult<string?>(hostFailures-- > 0 ? ProxyProblemCodes.SystemProxyConflict : null),
            "GetCapabilitiesAsync" => Task.FromResult(new ProxySystemProxyCapabilities(true, true, false, false)),
            _ => throw new Exception("Unexpected host settings call")
        });

        MihomoRuntimeService Runtime(MihomoPublicationStore selected) => new(transport, selected,
            new(selected, paths, paths), paths, new DirectDownloads());
        var receiptReader = new MihomoRuntimeReceiptReader(transport, paths);
        MihomoConfigurationPublicationService Service(MihomoPublicationStore selected) =>
            new(selected, new(selected, paths, paths), Runtime(selected), paths, new(paths, systemProxy), publicationProfiles);
        async Task<(Guid Operation, RuntimeConfigurationPlan Plan, long Revision)> PlanAsync(MihomoPublishedRuntime target)
        {
            var observed = await lifecycle.QueryAsync(default);
            var publication = await store.ReadAsync(default);
            return (Guid.NewGuid(), new(observed.Installation.InstallationId, observed.Installation.HostId, "mihomo",
                observed.Installation.StateRevision, observed.ActiveRevisionId!.Value, publication.Identity!,
                MihomoPublicationStore.Identity(target.Yaml), "server-real-" + Guid.NewGuid().ToString("N")), publication.Revision);
        }
        async Task RecoverAtDisabledStartupAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddSingleton(store);
            builder.Services.AddSingleton<IProxyPlatformPaths>(paths);
            builder.Services.AddSingleton<IDataProtectionProvider>(protection);
            builder.Services.AddTransient<IMihomoConfigurationPublicationRecovery>(_ => Service(store));
            var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
            await using var app = builder.Build();
            Check((await new ProxyModule().InspectDisabledStartupAsync(app.Services, context, default)).Count == 0,
                "The production disabled startup guard clears only an authoritatively reconciled publication");
        }
        var active = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
        try
        {
            var observed = await lifecycle.QueryAsync(default);
            if (observed.ConfigurationIdentity != MihomoPublicationStore.Identity(original.Yaml) || observed.ActiveRevisionId is null)
                throw new Exception("Server publication acceptance requires the already verified original installation");
            // Bootstrap is based on actual installed Helper evidence, not a fabricated pre-install confirm.
            var bootstrap = Guid.NewGuid();
            await store.BeginAsync(bootstrap, 0, original, default);
            Directory.CreateDirectory(paths.GetProtectedConfigurationDirectory());
            await File.WriteAllTextAsync(active, original.Yaml);
            await store.ConfirmAsync(bootstrap, observed.ConfigurationIdentity, default);
            var candidate = MihomoConfigurationPolicy.Describe(original.Yaml.Replace("mixed-port: " + originalPort,
                "mixed-port: " + (originalPort + 2), StringComparison.Ordinal)).Material;
            var first = await PlanAsync(candidate);
            var result = await Service(store).PublishAsync(first.Operation, first.Revision, first.Plan, candidate, default);
            Check(result.Outcome == MihomoPublicationOutcome.Confirmed && await probe(originalPort + 2),
                "Real Server publication confirms the changed listener and HTTP traffic");
            Check((await new MihomoRuntimeAuthorizationProvider(store, paths, paths).ReadAsync(default)).Published!.Yaml == candidate.Yaml
                && await File.ReadAllTextAsync(active) == candidate.Yaml,
                "Server grants and its file mirror use the actual Helper-confirmed candidate");
            Check((await Service(store).PublishAsync(first.Operation, first.Revision, first.Plan, candidate, default)).Outcome == MihomoPublicationOutcome.Confirmed
                && transport.ConfigureCalls == 1, "Real committed publication replays without another service restart");

            var second = await PlanAsync(original);
            transport.LoseNextConfigurationResponse = true;
            try
            {
                await Service(store).PublishAsync(second.Operation, second.Revision, second.Plan, original, default);
                throw new Exception("Lost real Helper response was not simulated");
            }
            catch (IOException) { count++; }
            Check((await store.ReadAsync(default)).Pending?.OperationId == second.Operation && await probe(originalPort),
                "Real Helper commits while the lost Server response leaves publication pending");
            store = new(paths, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys"))));
            await RecoverAtDisabledStartupAsync();
            Check((await store.ReadAsync(default)) is { Pending: null } recovered && recovered.Identity == second.Plan.CandidateIdentity
                && transport.ConfigureCalls == 2 && transport.RecoverCalls == 0
                && await File.ReadAllTextAsync(active) == original.Yaml,
                "Recreated Server startup recovery reconciles the real receipt without reapplying the configuration");

            var invalid = MihomoConfigurationPolicy.Describe(original.Yaml.Replace("MATCH,DIRECT", "UNSUPPORTED-RULE,DIRECT", StringComparison.Ordinal)).Material;
            var rejected = await PlanAsync(invalid);
            transport.LoseNextConfigurationResponse = true;
            try
            {
                await Service(store).PublishAsync(rejected.Operation, rejected.Revision, rejected.Plan, invalid, default);
                throw new Exception("Lost native compensation response was not simulated");
            }
            catch (IOException) { count++; }
            Check((await store.ReadAsync(default)).Pending?.OperationId == rejected.Operation && await probe(originalPort),
                "Native refusal and a lost response retain the original publication for startup reconciliation");
            await RecoverAtDisabledStartupAsync();
            Check(await probe(originalPort) && (await store.ReadAsync(default)).Pending is null
                && await File.ReadAllTextAsync(active) == original.Yaml && transport.RecoverCalls == 0,
                "Disabled startup reconciles actual native compensation without another Helper mutation");
            Check((await Service(store).PublishAsync(rejected.Operation, rejected.Revision, rejected.Plan, invalid, default)).Outcome == MihomoPublicationOutcome.Compensated
                && transport.ConfigureCalls == 3, "Real failed publication replays its measured failure without another transaction");

            var profiles = publicationProfiles;
            var profile = await profiles.UpsertAsync(null, "real application", "mihomo", null, default);
            var profileId = profile.Id;
            var engine = MihomoPublicationTransactionChecks.Stub<IProxyEngine>(method => method.Name == "get_EngineId"
                ? "mihomo" : throw new Exception("Application used fixed runtime engine"));
            var settings = MihomoPublicationTransactionChecks.Stub<IProxySettingsReader>(method => method.Name == "GetAsync"
                ? Task.FromResult(new ProxySettingsDto(false, false, false, false, false, "warning", originalPort + 2, false, "127.0.0.1", ProxyTunSettingsDto.Default))
                : throw new Exception("Unexpected settings call"));
            var application = new ProxyConfigurationTransactionService(paths, new ProxyEngineRegistry([engine]), profiles, paths,
                new MihomoControllerOptions { Endpoint = new("http://127.0.0.1:" + original.ControllerPort + "/") }, store,
                settingsService: settings, managedPublication: Service(store), managedRuntime: receiptReader);
            Check(await application.ApplyAsync(profileId, "rules:\n  - MATCH,DIRECT\n", default) is null
                && (await profiles.GetAsync(profileId, default)) is { IsActive: true, Revision: 2 } && await probe(originalPort + 2),
                "Application profile activation reaches real Helper transaction and serves HTTP on the selected listener");
            Check(await application.ApplyAsync(profileId, "rules:\n  - UNSUPPORTED-RULE,DIRECT\n", default) == ProxyProblemCodes.ConfigApplyFailed
                && (await profiles.GetAsync(profileId, default)) is { IsActive: true, Revision: 2 } && await probe(originalPort + 2) && (await store.ReadAsync(default)).Pending is null,
                "Application native rejection preserves real published configuration and profile selection");
            var nextProfile = await profiles.UpsertAsync(null, "recovered selection", "mihomo", null, default);
            await using (var database = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(root, "profiles.db")))
            {
                await database.OpenAsync();
                await using var trigger = database.CreateCommand();
                trigger.CommandText = "CREATE TRIGGER reject_profile_publication BEFORE UPDATE ON proxy_profile_publication BEGIN SELECT RAISE(ABORT, 'injected marker failure'); END;";
                await trigger.ExecuteNonQueryAsync();
                var selectedYaml = "rules:\n  - DOMAIN,example.test,DIRECT\n  - MATCH,DIRECT\n";
                Check(await application.ApplyAsync(nextProfile.Id, selectedYaml, default) == ProxyProblemCodes.RecoveryRequired
                    && (await store.ReadAsync(default)).Pending?.Profile?.ProfileId == nextProfile.Id
                    && (await profiles.GetAsync(profileId, default)) is { IsActive: true, Revision: 2 }
                    && !(await profiles.GetAsync(nextProfile.Id, default))!.IsActive && await probe(originalPort + 2),
                    "Real runtime commits while SQLite marker failure rolls back profile selection and retains pending intent");
                trigger.CommandText = "DROP TRIGGER reject_profile_publication;";
                await trigger.ExecuteNonQueryAsync();
            }
            var beforeProfileRecovery = transport.ConfigureCalls;
            var originalProfileOperation = (await store.ReadAsync(default)).Pending!.OperationId;
            store = new(paths, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys"))));
            publicationProfiles = await MihomoProfilePublicationChecks.CreateAsync(root);
            await VerifySchedulerDrainAsync(store, () => Service(store), transport, originalProfileOperation, () => probe(originalPort + 2));
            Check((await store.ReadAsync(default)).Pending is null && transport.ConfigureCalls == beforeProfileRecovery
                && (await profiles.GetAsync(nextProfile.Id, default)) is { IsActive: true, Revision: 2 }
                && !(await profiles.GetAsync(profileId, default))!.IsActive && await probe(originalPort + 2),
                "Server restart reconciles real SQLite profile selection without repeating native configuration");
            var controllerServices = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            controllerServices.Configuration["Proxy:Mihomo:Controller:Endpoint"] = $"http://127.0.0.1:{original.ControllerPort}/";
            controllerServices.AddProxyRuntimeServices();
            var versionRequests = 0;
            controllerServices.Services.AddHttpClient<IMihomoControllerClient, MihomoControllerClient>()
                .AddHttpMessageHandler(() => new VersionCountingHandler(() => Interlocked.Increment(ref versionRequests)));
            controllerServices.Services.AddSingleton<IDataProtectionProvider>(protection);
            var controllerSecret = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "data")).FullName, "proxy-controller.secret");
            await File.WriteAllTextAsync(controllerSecret, protection.CreateProtector("RelaxKonOS.Proxy.ControllerSecret.v1").Protect(original.ControllerSecret));
            await using (var controllers = controllerServices.Services.BuildServiceProvider())
            {
                var manager = new MihomoRuntimeStatusReader(controllers.GetRequiredService<IServiceScopeFactory>(),
                    new(paths), new(store, transport, paths));
                var beforeStatus = await lifecycle.QueryAsync(default);
                var firstStatus = await manager.GetAsync(MihomoEngine.Id, default);
                var nextStatus = await manager.GetAsync(MihomoEngine.Id, default);
                Check(firstStatus is { State: ProxyRuntimeState.Running, IntegrityVerified: true } && firstStatus == nextStatus && versionRequests == 2
                    && (await lifecycle.QueryAsync(default)).Installation.StateRevision == beforeStatus.Installation.StateRevision
                    && transport.ConfigureCalls == beforeProfileRecovery && await probe(originalPort + 2),
                    "Scoped runtime status probes the real authenticated controller and leaves forwarding and publication intact.");
                Console.WriteLine("PASS MIHOMO SCOPED RUNTIME STATUS: real controller and forwarding; no configuration mutation");
                await using var overviewScope = controllers.CreateAsyncScope();
                var controller = overviewScope.ServiceProvider.GetRequiredService<IMihomoControllerClient>();
                var safety = new ProxyTunSafetyService(paths, new HostProxyNetworkSafetyPlatform());
                var recovery = new CountingRecoveryReader(safety);
                Task<MihomoRuntimeObservation> ObserveRuntimeAsync(CancellationToken ct) => manager.GetObservedAsync(controller.IsReachableAsync, ct);
                var overview = new ProxyOverviewService(ObserveRuntimeAsync);
                var platform = new ProxyPlatformService(systemProxy);
                async Task<ProxyOverviewDto> ReadOverviewAsync(CancellationToken ct)
                {
                    using var code = ProxyConnectionExecutionGate.Shared.AcquireOverview();
                    return await overview.GetAsync(code, controller, platform, publicationProfiles.GetActiveAsync, recovery, ct);
                }
                Exception? retainedFailure = null;
                using (var code = ProxyConnectionExecutionGate.Shared.AcquireOverview())
                {
                    try
                    {
                        await overview.GetAsync(code, controller, platform,
                            _ => Task.FromException<ProxyProfileDto?>(new IOException("private-overview-fixture-error")), recovery, default);
                    }
                    catch (InvalidOperationException error) when (error.Message == "Proxy overview observation failed.")
                    { retainedFailure = error; }
                }
                IReadOnlyList<ProxyConnectionDto>? retainedConnections = null;
                ProxyOverviewDto? retainedOverview = null;
                Check(await probeWithConnection(originalPort + 2, async () =>
                {
                    var observation = await ReadOverviewAsync(default);
                    var detail = await controller.GetConnectionsAsync(default);
                    if (observation.ActiveProfile?.Id != nextProfile.Id || observation.ActiveConnections < 1
                        || observation.Runtime.State != ProxyRuntimeState.Running || versionRequests != 3 || recovery.Reads != 1
                        || !detail.Succeeded || observation.ActiveConnections != detail.Value!.Count
                        || observation.Health.State != ProxyHealthState.Healthy)
                        throw new InvalidOperationException("Overview must read the original active profile and count the real held proxy connection.");
                    retainedConnections = detail.Value;
                    retainedOverview = observation;
                }) && transport.ConfigureCalls == beforeProfileRecovery,
                    "Overview observes real forwarding without invoking publication.");
                Console.WriteLine("PASS MIHOMO SCOPED OVERVIEW: real held connection counted and original profile observed");
                Console.WriteLine("PASS MIHOMO SINGLE OVERVIEW OBSERVATION: one real /version and one safety read; standalone status observes on every call");
                var originalParser = ProxyConnectionExecutionGate.Shared.CurrentCode
                    ?? throw new InvalidOperationException("The actual overview must load its parser generation.");
                using var parserBudget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                while (ProxyConnectionExecutionGate.Shared.CurrentCode is not null)
                    await Task.Delay(250, parserBudget.Token);
                for (var i = 0; originalParser.IsAlive && i < 20; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    await Task.Delay(20, parserBudget.Token);
                }
                Check(!originalParser.IsAlive && retainedOverview is { ActiveConnections: > 0 }
                    && retainedFailure is { InnerException: null }
                    && !retainedFailure.ToString().Contains("private-overview-fixture-error", StringComparison.Ordinal)
                    && retainedOverview.ActiveProfile?.Id == nextProfile.Id && retainedConnections is { Count: > 0 }
                    && !string.IsNullOrEmpty(retainedConnections[0].Id) && await probe(originalPort + 2),
                    "Idle parser and projection code must collect while the Server retains projected values and native forwarding continues.");
                Console.WriteLine("PASS MIHOMO RETAINED CONNECTION PROJECTION: Server kept real connection DTOs while the old generation collected");
                Console.WriteLine("PASS MIHOMO RETAINED OVERVIEW PROJECTION: Server kept the overview DTO while shared read code collected");
                Console.WriteLine("PASS MIHOMO RETAINED OVERVIEW FAILURE: neutral Host failure kept while shared read code collected");
                Check(await probeWithConnection(originalPort + 2, async () =>
                {
                    var observation = await ReadOverviewAsync(parserBudget.Token);
                    if (observation.ActiveConnections < 1 || versionRequests != 4 || recovery.Reads != 2
                        || ProxyConnectionExecutionGate.Shared.CurrentCode is not { } next
                        || ReferenceEquals(originalParser, next))
                        throw new InvalidOperationException("A real connection must wake a new parser generation.");
                }) && transport.ConfigureCalls == beforeProfileRecovery,
                    "New parser generation must observe the actual connection without replaying configuration.");
                Console.WriteLine("PASS MIHOMO CONNECTION PARSER IDLE: natural idle release, collected code, live traffic and fresh parser generation");
                var originalCredential = await File.ReadAllBytesAsync(controllerSecret);
                try
                {
                    var protector = protection.CreateProtector("RelaxKonOS.Proxy.ControllerSecret.v1");
                    await File.WriteAllTextAsync(controllerSecret, protector.Protect(Guid.NewGuid().ToString("N")));
                    var unauthorized = await ReadOverviewAsync(default);
                    Check(unauthorized.Runtime.State == ProxyRuntimeState.Running
                        && unauthorized.Health is { ControllerReachable: false, ProblemCode: ProxyProblemCodes.ControllerAuthenticationFailed }
                        && unauthorized.ActiveConnections == 0 && versionRequests == 5 && recovery.Reads == 3,
                        "One real unauthorized observation distinguishes the listening runtime from degraded management health.");
                }
                finally { await File.WriteAllBytesAsync(controllerSecret, originalCredential); }
                Check(await probe(originalPort + 2) && transport.ConfigureCalls == beforeProfileRecovery,
                    "Controller authentication failure does not alter live proxy traffic or publication.");
                Console.WriteLine("PASS MIHOMO SINGLE OVERVIEW AUTH FAILURE: one real 401, live forwarding and unchanged publication");
            }
            var noOpApplication = new ProxyConfigurationTransactionService(paths, new ProxyEngineRegistry([engine]), publicationProfiles, paths,
                new MihomoControllerOptions { Endpoint = new("http://127.0.0.1:" + original.ControllerPort + "/") }, store,
                settingsService: settings, managedPublication: Service(store), managedRuntime: receiptReader);
            var beforeSelectionOnly = await lifecycle.QueryAsync(default);
            Check(await noOpApplication.ActivateStoredAsync(nextProfile.Id, default) is null && transport.ConfigureCalls == beforeProfileRecovery
                && (await lifecycle.QueryAsync(default)).Installation.StateRevision == beforeSelectionOnly.Installation.StateRevision,
                "Selecting identical published profile reconciles metadata without a real native transaction");
            var restore = await PlanAsync(original);
            if ((await Service(store).PublishAsync(restore.Operation, restore.Revision, restore.Plan, original, default)).Outcome != MihomoPublicationOutcome.Confirmed)
                throw new Exception("Could not restore original configuration after application acceptance");

            MihomoSettingsService SettingsService() => new(paths,
                MihomoPublicationTransactionChecks.Stub<IMihomoControllerClient>(_ => throw new Exception("Settings used direct controller")),
                paths, new MihomoControllerOptions(), systemProxy, store,
                managedPublication: Service(store), managedRuntime: receiptReader, settingsFiles: new(paths, systemProxy));
            var settingsRequest = new UpdateProxySettingsRequest(false, false, false, false, false, "warning", originalPort + 4);
            hostFailures = 1;
            var settingsProblem = await SettingsService().UpdateAsync(settingsRequest, default);
            var pendingSettings = (await store.ReadAsync(default)).Pending;
            Check(settingsProblem == ProxyProblemCodes.RecoveryRequired && pendingSettings?.Settings is not null
                && await probe(originalPort + 4) && !File.Exists(Path.Combine(paths.GetStateDirectory(), "mihomo-settings.json")),
                "Real runtime commit plus failed host-settings effect leaves exact intent pending");
            var beforeRecovery = transport.ConfigureCalls;
            store = new(paths, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys"))));
            using var recoveryServices3 = new PublicationRecoveryServices(store, () => Service(store), () => new(store, new(store, paths, paths), Runtime(store), paths), () => new(store, Runtime(store), paths, new(paths, systemProxy)));
            {
                var startup = new MihomoPublicationRecoveryService(recoveryServices3.Scopes, NullLogger<MihomoPublicationRecoveryService>.Instance);
                await startup.ReconcileAsync(default).WaitAsync(TimeSpan.FromSeconds(15));
            }
            Check((await store.ReadAsync(default)).Pending is null && (await new ProxySettingsReader(paths).GetAsync(default)).MixedPort == originalPort + 4
                && transport.ConfigureCalls == beforeRecovery && await probe(originalPort + 4),
                "Startup settings recovery confirms real Helper receipt and resumes settings without native replay");
            var beforeMetadata = await lifecycle.QueryAsync(default);
            Check(await SettingsService().UpdateAsync(settingsRequest with { AllowInsecureSubscriptionSources = true }, default) is null
                && transport.ConfigureCalls == beforeRecovery && (await lifecycle.QueryAsync(default)).Installation.StateRevision == beforeMetadata.Installation.StateRevision
                && (await new ProxySettingsReader(paths).GetAsync(default)).AllowInsecureSubscriptionSources && await probe(originalPort + 4),
                "Host-only settings persist without a real runtime transaction or service restart");
            restore = await PlanAsync(original);
            if ((await Service(store).PublishAsync(restore.Operation, restore.Revision, restore.Plan, original, default)).Outcome != MihomoPublicationOutcome.Confirmed)
                throw new Exception("Could not restore original configuration after settings acceptance");
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new Exception("Server acceptance cleanup escaped its root");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
        return count;
    }

    private static async Task VerifySchedulerDrainAsync(MihomoPublicationStore store, Func<MihomoConfigurationPublicationService> service,
        Transport transport, Guid operation, Func<Task<bool>> probe)
    {
        var budget = TimeSpan.FromSeconds(15);
        var cleanup = new ScopeBarrier();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddScoped(_ => cleanup);
        services.AddTransient<MihomoPublicationRecoveryService>();
        services.AddTransient<IMihomoConfigurationPublicationRecovery>(scope =>
        { _ = scope.GetRequiredService<ScopeBarrier>(); return service(); });
        await using var provider = services.BuildServiceProvider();
        using var scheduler = new MihomoPublicationRecoveryScheduler(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MihomoPublicationRecoveryScheduler>.Instance);
        transport.HoldQueryFor = operation;
        await scheduler.StartAsync(default);
        try
        {
            await transport.QueryObserved.Task.WaitAsync(budget);
            using var deadline = new CancellationTokenSource();
            deadline.Cancel();
            var stopping = scheduler.StopAsync(deadline.Token);
            if (stopping.IsCompleted || store.Gate.CurrentCount != 0
                || (await store.ReadAsync(default)).Pending?.OperationId != operation || !await probe())
                throw new InvalidOperationException("The original response, pending publication and working listener must survive the stop deadline.");
            transport.DeliverQuery.SetResult();
            await cleanup.Entered.Task.WaitAsync(budget);
            if (stopping.IsCompleted || (await store.ReadAsync(default)).Pending is not null)
                throw new InvalidOperationException("Stop must wait for scope cleanup after the original publication commits.");
            cleanup.Release.SetResult();
            await stopping.WaitAsync(budget);
            Console.WriteLine($"PASS MIHOMO PUBLICATION RECOVERY DRAIN: operation={operation}; original Helper receipt and scope drained");
        }
        finally
        {
            transport.DeliverQuery.TrySetResult();
            cleanup.Release.TrySetResult();
            await scheduler.StopAsync(default).WaitAsync(budget);
        }
    }

    private sealed class ScopeBarrier : IAsyncDisposable
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask DisposeAsync() { Entered.SetResult(); await Release.Task; }
    }

    private sealed class Transport(MihomoRuntimeLifecycle lifecycle) : IPrivilegedOperationTransport
    {
        internal bool LoseNextConfigurationResponse;
        internal int ConfigureCalls, RecoverCalls;
        internal Guid? HoldQueryFor;
        internal readonly TaskCompletionSource QueryObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DeliverQuery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            request = request with { OperationId = request.OperationId ?? Guid.NewGuid() };
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeConfigure) ConfigureCalls++;
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeRecover) RecoverCalls++;
            var result = await MihomoRuntimeOperations.ExecuteAsync(request, () => lifecycle);
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeQuery && request.OperationId == HoldQueryFor)
            {
                HoldQueryFor = null;
                QueryObserved.SetResult();
                await DeliverQuery.Task.WaitAsync(ct);
            }
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeConfigure && LoseNextConfigurationResponse)
            {
                LoseNextConfigurationResponse = false;
                throw new IOException("Simulated loss after real Helper execution");
            }
            return result;
        }
    }
    private sealed class VersionCountingHandler(Action observed) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.AbsolutePath == "/version") observed();
            return base.SendAsync(request, ct);
        }
    }

    private sealed class CountingRecoveryReader(IProxyRecoveryService recovery) : IProxyRecoveryService
    {
        internal int Reads { get; private set; }
        public Task<ProxyRecoveryStatusDto> GetStatusAsync(CancellationToken ct)
        {
            Reads++;
            return recovery.GetStatusAsync(ct);
        }
    }

    private sealed class Paths(string root, string secret) : IProxyPlatformPaths, IProxyControllerSecretStore, IServerModeResolver
    {
        public string GetEngineVersionsDirectory(string engineId) => Path.Combine(root, "versions");
        public string GetProtectedConfigurationDirectory() => Path.Combine(root, "config");
        public string GetEngineDataDirectory(string engineId) => Path.Combine(root, "data");
        public string GetStateDirectory() => Path.Combine(root, "state");
        public string GetSanitizedLogDirectory() => Path.Combine(root, "logs");
        public Task<string?> ReadExistingAsync(CancellationToken ct) => Task.FromResult<string?>(secret);
        public Task<string> GetOrCreateAsync(CancellationToken ct) => throw new Exception("Server publication generated credentials");
        public ServerMode Mode => ServerMode.System;
        public bool Supports(ServerHostFeature feature) => true;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    }
}
