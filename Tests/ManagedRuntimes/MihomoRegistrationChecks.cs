using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Server.Installations;
using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Startup;

internal static class MihomoRegistrationChecks
{
    internal static async Task<int> RunAsync()
    {
        using var temporary = new TemporaryDirectory();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            { ApplicationName = typeof(MihomoRegistrationChecks).Assembly.GetName().Name, ContentRootPath = temporary.Path, EnvironmentName = "Testing" });
        // Host backbone first (runtime, publication ledger, settings), then the proxy module's own
        // surface: the profile/subscription/configuration services moved there, so this fixture has to
        // register the same pair the Host does instead of the old single AddProxyServices call.
        builder.AddProxyRuntimeServices();
        var coreOwnsValidator = builder.Services.Any(item => item.ServiceType == typeof(IMihomoConfigurationValidator));
        var coreOwnsTunDispatch = builder.Services.Any(item => item.ServiceType == typeof(MihomoManagedTunOperations)
            || item.ServiceType == typeof(MihomoNamedTunStartRecovery));
        ServerModules.CreateManaged(new HashSet<string> { ServerModules.ProxyId }).ConfigureServices(builder,
            ServerModuleContext.Create(builder.Configuration, builder.Environment, "sqlite"));
        var proxyHostedFactories = builder.Services.Where(item => item.ServiceType == typeof(IHostedService)
            && item.ImplementationFactory is not null).ToArray();
        builder.AddDeploymentServices();
        var database = System.IO.Path.Combine(temporary.Path, "registration.db");
        builder.Services.Configure<RelaxKonOS.Server.Storage.StorageOptions>(options => options.DatabasePath = database);
        builder.AddServerStorage(new RelaxKonOS.Server.Storage.StorageOptions { DatabasePath = database }, "sqlite");
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddSingleton<IServerModeResolver>(new FixedMode());
        builder.Services.AddSingleton<IPrivilegedOperationTransport>(new NoHelper());
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IOutboundProxyHttpClientFactory>(new NoDownloads());
        // Mirrors HostServiceRegistration: the resolver is looked up per operation, and that deferral is
        // exactly what keeps the runtime service, its publication coordinator and the proxy settings
        // service out of a construction cycle. Registering a stub here would hide that.
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerProxyResolver, RelaxKonOS.Server.Docker.DockerProxyResolver>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ManagedRuntimes.IRuntimeDownloadProxyProvider>(container =>
            new RelaxKonOS.Server.ManagedRuntimes.RuntimeDownloadProxyProvider(
                () => container.GetRequiredService<RelaxKonOS.Server.Docker.IDockerProxyResolver>()));
        await using var services = builder.Services.BuildServiceProvider();
        using var scope = services.CreateScope();
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        var store = services.GetRequiredService<MihomoPublicationStore>();
        Check(ReferenceEquals(store, services.GetRequiredService<MihomoPublicationStore>()), "Default publication authority and gate are singleton");
        Check(services.GetRequiredService<MihomoRuntimeRecoveryService>() is not null
            && services.GetRequiredService<MihomoRuntimeService>() is not null
            && services.GetRequiredService<MihomoConfigurationPublicationService>() is not null
            && services.GetRequiredService<MihomoInstallationPublicationService>() is not null
            && services.GetRequiredService<MihomoInstallationBootstrapService>() is not null
            && services.GetRequiredService<MihomoRemovalPublicationService>() is not null,
            "Default runtime and publication coordinators resolve with the current contracts");
        Check(new[] { typeof(MihomoConfigurationPublicationService), typeof(MihomoInstallationPublicationService), typeof(MihomoRemovalPublicationService), typeof(MihomoRuntimeRecoveryService) }
            .Append(typeof(MihomoInstallationBootstrapService))
            .All(type => builder.Services.Single(x => x.ServiceType == type).Lifetime == ServiceLifetime.Transient),
            "Publication coordinators and manual recovery have per-resolution instances and retain the singleton publication authority");
        Check(scope.ServiceProvider.GetRequiredService<IProxyConfigurationTransactionService>() is ProxyConfigurationTransactionService
            && services.GetRequiredService<IProxySettingsReader>() is ProxySettingsReader,
            "Default profile transactions and lightweight settings reader resolve their current contracts");
        Check(services.GetRequiredService<IProxySystemProxyReconciler>() is MihomoSystemProxyReconciler
            && services.GetRequiredService<IProxyTunRuntimeController>() is ScopedProxyTunRuntimeController
            && builder.Services.Single(item => item.ServiceType == typeof(MihomoTunRuntimeController)).Lifetime == ServiceLifetime.Scoped
            && services.GetRequiredService<IMihomoRuntimeReceiptReader>() is MihomoRuntimeReceiptReader,
            "Guard and TUN have narrow executors and the receipt port reads the original Helper owner independently");
        IServiceCollection receiptServices = new ServiceCollection();
        receiptServices.Add(builder.Services.Single(item => item.ServiceType == typeof(IMihomoRuntimeReceiptReader)));
        receiptServices.AddSingleton<IServerModeResolver>(new FixedMode());
        receiptServices.AddSingleton<IPrivilegedOperationTransport>(new NoHelper());
        await using (var receiptProvider = receiptServices.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }))
            Check(receiptProvider.GetRequiredService<IMihomoRuntimeReceiptReader>() is MihomoRuntimeReceiptReader,
                "The registered receipt reader resolves with only the Helper transport and host mode");
        Check(builder.Services.Single(x => x.ServiceType == typeof(IProxySettingsWriter)).Lifetime == ServiceLifetime.Scoped
            && scope.ServiceProvider.GetRequiredService<IProxySettingsWriter>() is MihomoSettingsService
            && scope.ServiceProvider.GetRequiredService<IMihomoApplicationConfigurationPublisher>() is MihomoConfigurationPublicationService
            && services.GetRequiredService<IMihomoConfigurationPublicationRecovery>() is MihomoConfigurationPublicationService,
            "Management resolves scoped write ports and Core resolves its configuration recovery port");
        Check(builder.Services.Count(item => item.ServiceType == typeof(IHostedService)
                && item.ImplementationType == typeof(SystemProxyGuardHostedService)) == 1,
            "Proxy registration keeps exactly one Core system proxy guard");
        Check(builder.Services.Single(item => item.ServiceType == typeof(ProxySubscriptionOperations)).Lifetime == ServiceLifetime.Scoped
            && builder.Services.Single(item => item.ServiceType == typeof(ProxyLifecycleOperations)).Lifetime == ServiceLifetime.Scoped
            && proxyHostedFactories.Count(item => ReferenceEquals(item.ImplementationFactory!(services),
                services.GetRequiredService<ProxyOperationStore>())) == 1,
            "Scoped acceptance uses the same singleton operation ledger registered once for shutdown drain");
        Check(!coreOwnsTunDispatch
            && builder.Services.Single(item => item.ServiceType == typeof(MihomoManagedTunOperations)).Lifetime == ServiceLifetime.Scoped
            && builder.Services.Single(item => item.ServiceType == typeof(MihomoNamedTunStartRecovery)).Lifetime == ServiceLifetime.Scoped
            && ReferenceEquals(scope.ServiceProvider.GetRequiredService<MihomoManagedTunOperations>(),
                scope.ServiceProvider.GetRequiredService<MihomoManagedTunOperations>())
            && ReferenceEquals(scope.ServiceProvider.GetRequiredService<MihomoNamedTunStartRecovery>(),
                scope.ServiceProvider.GetRequiredService<MihomoNamedTunStartRecovery>()),
            "Only Proxy registers the scoped managed TUN dispatch and named start recovery executors");
        Check(builder.Services.Single(item => item.ServiceType == typeof(RelaxKonOS.Server.ProxyServer.ProxyServerService)).Lifetime
                == ServiceLifetime.Transient
            && builder.Services.Single(item => item.ServiceType == typeof(RelaxKonOS.Server.ProxyServer.IProxyServerExpiryExecutor)).Lifetime
                == ServiceLifetime.Transient,
            "Inbound management and expiry executors are not rooted as singleton services");
        Check(builder.Services.Count(item => item.ServiceType == typeof(IHostedService)
                && item.ImplementationType == typeof(RelaxKonOS.Server.ProxyServer.ProxyServerExpiryService)) == 1,
            "Proxy registration keeps exactly one expiry scheduler");
        // Only the Mihomo ledger entry is factory-registered; instantiate exactly that one so the
        // fixture never has to construct unrelated package installers.
        var descriptor = builder.Services.Single(x => x.ServiceType == typeof(RelaxKonOS.Server.Installations.IInstallationService)
            && x.ImplementationFactory is not null);
        var ledger = (RelaxKonOS.Server.Installations.IInstallationService)descriptor.ImplementationFactory!(services)!;
        Check(ledger.Id == RelaxKonOS.Protocol.Installations.InstallationServiceId.Mihomo
            && ReferenceEquals(ledger, services.GetRequiredService<RelaxKonOS.Server.Installations.MihomoRuntimeInstallationService>()),
            "Mihomo installs only through the managed catalog adapter, registered once");
        Check(builder.Services.Any(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(MihomoPublicationRecoveryScheduler)),
            "Default startup registers pending publication recovery");
        // Resolving the runtime service while the real Docker proxy resolver is registered is the assertion:
        // an eager resolver dependency closes the cycle this fixture used to hide with a stub.
        Check(services.GetRequiredService<RelaxKonOS.Server.ManagedRuntimes.MihomoRuntimeService>() is not null,
            "Runtime service resolves alongside the real download-proxy resolver without a construction cycle");
        builder.Services.AddSingleton<MihomoSettingsService>(_ => throw new Exception("Safety executor constructed the settings writer"));
        builder.Services.AddTransient<MihomoInstallationBootstrapService>(_ => throw new Exception("The idle ledger constructed the installation bootstrap."));
        builder.Services.AddTransient<MihomoInstallationPublicationService>(_ => throw new Exception("The idle ledger constructed an installation publisher."));
        builder.Services.AddTransient<MihomoRemovalPublicationService>(_ => throw new Exception("The idle ledger constructed a removal publisher."));
        builder.Services.AddScoped<MihomoRuntimeManager>(_ => throw new Exception("Status resolved the official archive installer."));
        var paths = new ProbePaths(temporary.Path);
        builder.Services.AddSingleton<IProxyPlatformPaths>(paths);
        var handlers = System.Threading.Channels.Channel.CreateUnbounded<ControllerProbeHandler>();
        var handlerCount = 0;
        async Task<ControllerProbeHandler> NextHandlerAsync() =>
            await handlers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        builder.Services.AddTransient<IMihomoControllerClient>(provider =>
        {
            var handler = new ControllerProbeHandler();
            Interlocked.Increment(ref handlerCount);
            handlers.Writer.TryWrite(handler);
            return new MihomoControllerClient(new HttpClient(handler),
                provider.GetRequiredService<IProxyControllerSecretStore>(), provider.GetRequiredService<MihomoControllerOptions>());
        });
        await using (var tunServices = builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }))
        {
            using var recovery = ActivatorUtilities.CreateInstance<ProxyRecoveryHostedService>(tunServices);
            var runtime = tunServices.GetRequiredService<IProxyTunRuntimeController>();
            Check(tunServices.GetRequiredService<IProxyTunSafetyService>() is ProxyTunSafetyService
                && runtime is ScopedProxyTunRuntimeController && handlerCount == 0,
                "Production TUN startup constructs neither a settings writer nor an HTTP controller");
            for (var call = 1; call <= 2; call++)
            {
                var pending = runtime.IsEnabledAsync(default);
                var handler = await NextHandlerAsync();
                await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!pending.IsCompleted && !handler.Disposed, "TUN scope keeps the controller through the actual response task");
                handler.Reply.SetResult(new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") });
                Check((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded && handler.Disposed && handlerCount == call,
                    "Each completed TUN observation disposes its own controller scope");
            }
            var manager = tunServices.GetRequiredService<IProxyRuntimeManager>();
            Check(ReferenceEquals(manager, tunServices.GetRequiredService<MihomoRuntimeStatusReader>()) && handlerCount == 2,
                "Core status and the Proxy sampler share a reader without constructing the installer or controller.");
            await using (var first = tunServices.CreateAsyncScope())
            await using (var second = tunServices.CreateAsyncScope())
            {
                var validator = first.ServiceProvider.GetRequiredService<IMihomoConfigurationValidator>();
                Check(!coreOwnsValidator && validator is MihomoConfigurationValidator
                    && !ReferenceEquals(validator, second.ServiceProvider.GetRequiredService<IMihomoConfigurationValidator>())
                    && ReferenceEquals(first.ServiceProvider.GetRequiredService<MihomoOfficialRuntimeState>(),
                        second.ServiceProvider.GetRequiredService<MihomoOfficialRuntimeState>()),
                    "Proxy owns the scoped validator while Core keeps the one shared official runtime state.");
            }
            const string version = "v1.19.30";
            var release = Directory.CreateDirectory(System.IO.Path.Combine(paths.GetEngineVersionsDirectory(MihomoEngine.Id), version + "-" + MihomoRuntimeManifest.CurrentRid()));
            File.WriteAllText(System.IO.Path.Combine(release.FullName, OperatingSystem.IsWindows() ? "mihomo.exe" : "mihomo"), "");
            File.WriteAllText(System.IO.Path.Combine(paths.GetStateDirectory(), "mihomo-runtime.json"),
                System.Text.Json.JsonSerializer.Serialize(new { ActiveVersion = version, ActivatedAt = DateTimeOffset.UtcNow }));
            var status = manager.GetAsync(MihomoEngine.Id, default);
            var statusHandler = await NextHandlerAsync();
            await statusHandler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!status.IsCompleted && !statusHandler.Disposed, "Runtime status retains the controller until the actual response.");
            statusHandler.Reply.SetResult(new(System.Net.HttpStatusCode.Unauthorized) { Content = new StringContent("") });
            Check((await status.WaitAsync(TimeSpan.FromSeconds(5))).State == ProxyRuntimeState.Running && statusHandler.Disposed,
                "A listening controller still counts as running on HTTP 401, and its scope is released.");
            var unauthorized = ControllerResult<bool>.Failure(ProxyProblemCodes.ControllerAuthenticationFailed);
            var runtimeFromObservation = await ((MihomoRuntimeStatusReader)manager).GetObservedAsync(_ => Task.FromResult(unauthorized), default);
            var recoveryHealth = await MihomoEngine.ProjectHealthAsync(null!, unauthorized, new(true, true, null), default);
            Check(runtimeFromObservation.Runtime.State == ProxyRuntimeState.Running && runtimeFromObservation.Controller == unauthorized && handlerCount == 3
                && recoveryHealth is { ControllerReachable: false, State: ProxyHealthState.RecoveryRequired, TunState: ProxyTunState.Recovering },
                "A shared unauthorized observation reports a listening runtime without constructing a second client or masking Core recovery.");
            using var cancelled = new CancellationTokenSource();
            status = manager.GetAsync(MihomoEngine.Id, cancelled.Token);
            var cancelledHandler = await NextHandlerAsync();
            await cancelledHandler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            try { await status.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Cancelled status reported a runtime state."); }
            catch (OperationCanceledException) when (status.IsCanceled) { }
            Check(cancelledHandler.Disposed, "A cancelled probe releases only its own controller scope.");
        }
        builder.Services.AddSingleton<IMihomoControllerClient>(_ => throw new Exception("Guard constructed a controller client"));
        using var guardServices = builder.Services.BuildServiceProvider();
        using var guard = ActivatorUtilities.CreateInstance<SystemProxyGuardHostedService>(guardServices);
        Check(guardServices.GetRequiredService<IProxySystemProxyReconciler>() is MihomoSystemProxyReconciler,
            "Production guard constructs without the settings writer, TUN controller or direct controller client");
        await CheckOfficialInstallScopeAsync(builder, Check);
        await CheckResponsibilityWireAsync(Check);
        return count;
    }

    private static async Task CheckResponsibilityWireAsync(Action<bool, string> check)
    {
        var response = """{"PendingOperationId":null,"TunPublicationId":null}""";
        var transport = MihomoPublicationTransactionChecks.Stub<IPrivilegedOperationTransport>(_ =>
            Task.FromResult(new PrivilegedOperationResult(true,
                OutputBase64: Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(response)))));
        var reader = new MihomoRuntimeReceiptReader(transport, new FixedMode());
        check(await reader.QueryResponsibilityAsync(default) is { PendingOperationId: null, TunPublicationId: null },
            "An explicit empty Helper responsibility is readable.");
        foreach (var invalid in new[]
        {
            """{"PendingOperationId":null}""",
            """{"PendingOperationId":"00000000-0000-0000-0000-000000000001","PendingOperationId":null,"TunPublicationId":null}""",
            """{"PendingOperationId":null,"TunPublicationId":"00000000-0000-0000-0000-000000000000"}"""
        })
        {
            response = invalid;
            try
            {
                await reader.QueryResponsibilityAsync(default);
                throw new InvalidOperationException("Malformed Helper responsibility was accepted as evidence.");
            }
            catch (RuntimeLifecycleException error) when (error.ProblemCode == "runtime.helper_response_invalid")
            { check(true, "Missing, duplicate or empty ID evidence cannot authorize deactivation."); }
        }
    }

    private static async Task CheckOfficialInstallScopeAsync(WebApplicationBuilder builder, Action<bool, string> check)
    {
        var cleanup = new InstallScopeCleanup();
        var helper = new OriginalInstallQuery();
        var dispatches = 0;
        builder.Services.AddSingleton<IPrivilegedOperationTransport>(helper);
        builder.Services.AddScoped(_ => cleanup);
        builder.Services.AddScoped(provider =>
        {
            Interlocked.Increment(ref dispatches);
            _ = provider.GetRequiredService<InstallScopeCleanup>();
            return ActivatorUtilities.CreateInstance<MihomoRuntimeManager>(provider);
        });
        await using var services = builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var lifetime = new ManagedRuntimeLedgerFixture.Lifetime();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = budget.Token;
        var domain = services.GetRequiredService<MihomoRuntimeInstallationService>();
        var official = services.GetRequiredService<MihomoOfficialRuntimeState>();
        var store = services.GetRequiredService<InstallationOperationStore>();
        var files = services.GetRequiredService<InstallationFileReferenceStore>();
        const string actor = "official-install-scope";
        const string version = "v1.19.31";
        var source = Path.Combine(builder.Environment.ContentRootPath, "official-install.fixture");
        await File.WriteAllTextAsync(source, "the fixture cancels before copying", ct);
        var file = files.RegisterStaged(domain.Id, actor, source, "fixture", () => File.Delete(source));
        var request = new MihomoInstallationRequest(true, Version: version, FileReferenceId: file.Id);
        var options = JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var entry = store.Create(domain.Id, InstallationOperationKind.Install, actor, actor,
            InstallationOperationStore.Reference(JsonSerializer.Serialize(request)), domain.Resources, out _);
        var operation = store.Update(entry.Operation.OperationId, value => value with
        {
            State = InstallationOperationState.Running,
            Stage = InstallationStage.Copying,
            StartedAt = DateTimeOffset.UtcNow
        }, "started");
        var marker = official.InstallationOperationPath(operation.OperationId);
        var progress = new InstallCopyPause(operation.OperationId);
        check(dispatches == 0, "The root installation ledger resolves without an official archive installer.");
        using var interruption = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = domain.StartAsync(operation.Kind, request, actor, progress, interruption.Token);
        byte[] original;
        try
        {
            await progress.Entered.Task.WaitAsync(ct);
            original = await File.ReadAllBytesAsync(marker, ct);
            await domain.RequestCancelAsync(operation, ct);
            await cleanup.Entered.Task.WaitAsync(ct);
            check(dispatches == 1 && !running.IsCompleted && File.Exists(source),
                "The dispatched install retains its operation and source through asynchronous scope cleanup.");
        }
        finally
        {
            interruption.Cancel();
            cleanup.Release.TrySetResult();
        }
        try { await running.WaitAsync(ct); throw new Exception("Expected cancellation during archive preparation."); }
        catch (InstallationCancelledException) { }

        var coordinator = new InstallationCoordinator(store, [domain], lifetime);
        await coordinator.StartAsync(ct);
        var recovered = coordinator.Get(operation.OperationId)?.Operation;
        if (OperatingSystem.IsWindows())
            check(coordinator.Start(domain.Id, operation.Kind, options, actor, actor) == recovered,
                "The Windows official install key returns the original interrupted result.");
        else
        {
            try
            {
                coordinator.Start(domain.Id, operation.Kind, options, actor, "new-official-install");
                throw new InvalidOperationException("Linux accepted an official archive without a signed plan.");
            }
            catch (InstallationException error) when (error.ProblemCode == InstallationProblemCodes.InvalidRequest) { }
        }
        var recoveredMarker = await File.ReadAllBytesAsync(marker, ct);
        check(recovered?.OperationId == operation.OperationId && recovered.State == InstallationOperationState.Interrupted
            && dispatches == 1
            && (!OperatingSystem.IsLinux() || helper.OriginalQueries.SequenceEqual([operation.OperationId]))
            && original.SequenceEqual(recoveredMarker)
            && !File.Exists(source) && !Directory.Exists(official.VersionDirectory(version)),
            "The original install marker recovers without resolving a second installer or activating a release.");
        Console.WriteLine($"PASS MIHOMO OFFICIAL INSTALL SCOPE: original={operation.OperationId}; one installer scope; interrupted and recovered without redispatch");
    }

    private sealed class InstallCopyPause(Guid operationId) : IInstallationProgress
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid OperationId => operationId;

        public async Task ReportAsync(InstallationProgress progress, CancellationToken cancellationToken = default)
        {
            if (progress.Stage != InstallationStage.Copying) return;
            Entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class InstallScopeCleanup : IAsyncDisposable
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask DisposeAsync()
        {
            Entered.TrySetResult();
            await Release.Task;
        }
    }

    private sealed class OriginalInstallQuery : IPrivilegedOperationTransport
    {
        internal readonly List<Guid> OriginalQueries = [];
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            if (request is not { Operation: PrivilegedOperationKind.ComponentPackageQuery, OperationId: Guid id })
                throw new InvalidOperationException("The original installation lookup attempted a different Helper operation.");
            OriginalQueries.Add(id);
            return Task.FromResult(new PrivilegedOperationResult(false));
        }
    }

    private sealed class ProbePaths(string root) : IProxyPlatformPaths
    {
        public string GetStateDirectory() => root;
        public string GetProtectedConfigurationDirectory() => root;
        public string GetEngineVersionsDirectory(string engineId) => root;
        public string GetEngineDataDirectory(string engineId) => root;
        public string GetSanitizedLogDirectory() => root;
    }
    private sealed class ControllerProbeHandler : HttpMessageHandler
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<HttpResponseMessage> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return Reply.Task.WaitAsync(cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mihomo-registration-" + Guid.NewGuid().ToString("N"));
        internal TemporaryDirectory() => Directory.CreateDirectory(Path);
        // A pooled SQLite connection can keep the scratch file open briefly; a scratch directory is not
        // worth failing the check over, so a busy file is left to the OS instead of aborting the run.
        public void Dispose()
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try { Directory.Delete(Path, true); return; }
                catch (IOException) { Thread.Sleep(100); }
                catch (UnauthorizedAccessException) { Thread.Sleep(100); }
            }
        }
    }
    private sealed class NoHelper : IPrivilegedOperationTransport
    {
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default) =>
            throw new Exception("Resolving publication registrations invoked Helper");
    }
    private sealed class NoDownloads : RelaxKonOS.Server.Docker.IOutboundProxyHttpClientFactory
    {
        public Task<HttpClient> CreateAsync(RelaxKonOS.Server.Docker.OutboundProxyTarget target, TimeSpan timeout, CancellationToken ct = default) =>
            throw new Exception("Resolving publication registrations started a download");
    }
    private sealed class FixedMode : IServerModeResolver
    {
        public ServerMode Mode => ServerMode.System;
        public bool Supports(ServerHostFeature feature) => true;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    }
}
