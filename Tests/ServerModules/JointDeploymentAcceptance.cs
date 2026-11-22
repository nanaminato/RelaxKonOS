extern alias modulehelper;

using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Server.GatewayProxy;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.Tailscale;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.Modules;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Apps.AppInstaller.ViewModels;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Services.VirtualSystemDrive;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Deployment;
using RelaxKonOS.ManagedRuntimes.Catalog;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Protocol.Firewall;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.ServerCenter;
using RelaxKonOS.Protocol.Tunnels;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Runtimes;
using RelaxKonOS.Server.Secrets;
using RelaxKonOS.Server.Tunnels;
using RelaxKonOS.Server.WebServer;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Runtime;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.Docker;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Git;
using RelaxKonOS.Server.Firewall;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.Storage.Sqlite;

internal static class JointDeploymentAcceptance
{
    private const string ModuleId = "fixture-module";
    private const string AppIdText = "relaxkonos.welcome";

    public static void Run(string archive, IReadOnlyList<string> bundledModuleArchives) =>
        RunWithDispatcher(() => RunAsync(Path.GetFullPath(archive), bundledModuleArchives.Select(Path.GetFullPath).ToArray()));

    public static void RunSignedGit(string archive) => RunWithDispatcher(() => RunSignedExecutionAsync(Path.GetFullPath(archive),
        ServerModules.GitId, ServerCapabilities.Git, "Git", CollectibleGitExecution.AssemblyFile, CollectibleGitExecution.EntryType,
        new Dictionary<string, string> { [ServerCapabilities.Git] = GitApiRoutes.CapabilityContractVersion }, VerifySignedGitAsync));

    public static void RunSignedTunnels(string archive) => RunWithDispatcher(() => RunSignedExecutionAsync(Path.GetFullPath(archive),
        "relaxkonos.tunnels", ServerCapabilities.Tunnels, "Tunnels", RelaxKonOS.Server.Tunnels.CollectibleTunnelExecution.AssemblyFile,
        RelaxKonOS.Server.Tunnels.CollectibleTunnelExecution.EntryType, null, VerifySignedTunnelsAsync));

    public static void RunSignedTailscale(string archive) => RunWithDispatcher(() => RunSignedExecutionAsync(Path.GetFullPath(archive),
        "relaxkonos.tailscale", ServerCapabilities.Tailscale, "Tailscale", RelaxKonOS.Server.Tailscale.CollectibleTailscaleExecution.AssemblyFile,
        RelaxKonOS.Server.Tailscale.CollectibleTailscaleExecution.EntryType, null, VerifySignedTailscaleAsync));

    private static async Task VerifySignedTailscaleAsync(TestHost host, string _)
    {
        using var client = new HttpClient { BaseAddress = new Uri(host.Url) };
        var fixture = host.Services.GetRequiredService<IPrivilegedOperationTransport>() as TailscaleReadHelper;
        using var anonymous = await client.GetAsync(TailscaleEndpoints.Control);
        Require(anonymous.StatusCode == System.Net.HttpStatusCode.Unauthorized && (fixture is null || fixture.ReadCount == 0),
            "anonymous Tailscale control reads are rejected before reaching Helper");
        client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Observer);
        var snapshot = await client.GetFromJsonAsync<TailscaleControlSnapshot>(TailscaleEndpoints.Control, RelaxKonOSJsonOptions.Default);
        Require(fixture is null ? snapshot is { Revision.Length: 64 } : snapshot == TailscaleReadHelper.Snapshot && fixture.ReadCount == 1,
            "the HTTP control route reads through the joint-installed Tailscale executor and production Helper adapter");
        client.DefaultRequestHeaders.Remove("test-user");
        client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Admin);
        using var denied = await client.PostAsJsonAsync(TailscaleEndpoints.Operations,
            new TailscalePrepareRequest(Guid.NewGuid(), snapshot!.Revision, TailscaleAction.Disconnect), RelaxKonOSJsonOptions.Default);
        using var problem = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Require(denied.StatusCode == System.Net.HttpStatusCode.Forbidden
            && problem.RootElement.GetProperty("code").GetString() == "tailscale.elevation_required"
            && (fixture is null || fixture.ReadCount == 1),
            "the installed signed Tailscale executor rejects a controller without host-network elevation");
    }

    public static void RunSignedCertificates(string archive) => RunWithDispatcher(() => RunSignedExecutionAsync(Path.GetFullPath(archive),
        ServerModules.CertificatesId, ServerCapabilities.Certificates, "Certificates", AcmeExecution.AssemblyFile,
        AcmeExecution.EntryType, null, VerifySignedCertificatesAsync));

    private static async Task VerifySignedCertificatesAsync(TestHost _, string root)
    {
        var serverRoot = Path.Combine(root, "server");
        var fixture = Environment.GetEnvironmentVariable("RK_TEST_ACME_FIXTURE");
        var options = new CertificateOptions
        {
            DirectoryUrl = fixture is null ? "invalid-directory" : "https://localhost:14000/dir",
            StorageRoot = Path.Combine(serverRoot, "certificates"),
            ChallengeRoot = Path.Combine(serverRoot, "challenges"),
        };
        (WebApplicationBuilder Builder, ServerModules Modules) ConfigureHost()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = serverRoot });
            builder.Configuration["Storage:Provider"] = "memory";
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<FileHttp01ChallengeStore>();
            builder.Services.AddSingleton<CertificateMetadataRepository>();
            var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.CertificatesId }, InstalledModuleCatalog.Load(serverRoot));
            modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
            if (fixture is not null)
            {
                builder.Services.AddSingleton(_ => new DirectHttp01ChallengeStore("http://+:5002/.well-known/acme-challenge/"));
                builder.Services.AddSingleton<ICertificateStore, FileCertificateStore>();
                builder.Services.AddSingleton<HostOperationJournal>();
                builder.Services.AddSingleton<KestrelCertificateRegistry>();
                builder.Services.AddSingleton<CertificateDeploymentRepository>();
                builder.Services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(
                    new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
                builder.Services.AddSingleton<RelaxKonOS.Server.WebServer.IHostPrivilegeService, CertificateFixturePrivilege>();
                builder.Services.AddSingleton<IHostElevationSessionStore, CertificateFixtureElevation>();
                builder.Services.AddSingleton<IServerModeResolver>(new ServerModeResolver(builder.Configuration,
                    RelaxKonOS.Server.UserExecution.UserExecutionBackend.Helper));
                builder.Services.AddAuthentication("module-test")
                    .AddScheme<AuthenticationSchemeOptions, ModuleTestAuthentication>("module-test", _ => { });
                builder.Services.AddAuthorization();
                builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(
                    new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
            }
            return (builder, modules);
        }
        var (builder, modules) = ConfigureHost();
        await using var host = builder.Build();
        var execution = host.Services.GetRequiredService<AcmeExecution>();
        if (fixture is not null)
        {
            host.UseAuthentication();
            host.UseAuthorization();
            modules.MapEndpoints(host, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
            host.Urls.Add("http://127.0.0.1:0");
            await host.StartAsync();
            CertificateOperationDto completed;
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(host.Urls.Single()) };
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                const string domain = "localhost";
                client.DefaultRequestHeaders.Add("Idempotency-Key", "signed-acme-issuance");
                var request = new RequestCertificateRequest([domain], CertificateChallengeType.DirectHttp01,
                    "acceptance@example.test", true, PublicReachabilityConfirmed: true);
                client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Observer);
                using var forbidden = await client.PostAsJsonAsync(CertificateApiRoutes.Request, request, RelaxKonOSJsonOptions.Default, timeout.Token);
                Require(forbidden.StatusCode == System.Net.HttpStatusCode.Forbidden, "signed certificate issuance requires its elevation grant");
                client.DefaultRequestHeaders.Remove("test-user");
                client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Admin);
                using var started = await client.PostAsJsonAsync(CertificateApiRoutes.Request, request, RelaxKonOSJsonOptions.Default, timeout.Token);
                started.EnsureSuccessStatusCode();
                var receipt = (await started.Content.ReadFromJsonAsync<CertificateOperationDto>(RelaxKonOSJsonOptions.Default, timeout.Token))!;
                var status = CertificateApiRoutes.Operations.Replace("{operationId}", receipt.OperationId.ToString("D"));
                completed = receipt;
                while (completed.State is CertificateOperationState.Queued or CertificateOperationState.Running)
                {
                    await Task.Delay(100, timeout.Token);
                    completed = (await client.GetFromJsonAsync<CertificateOperationDto>(status, RelaxKonOSJsonOptions.Default, timeout.Token))!;
                }
                Require(completed.State == CertificateOperationState.Succeeded && !ReferenceEquals(execution, AcmeExecution.Shared),
                    $"the certificate management route completes real signed ACME issuance: {completed.ProblemCode}");
                using var replay = await client.PostAsJsonAsync(CertificateApiRoutes.Request, request, RelaxKonOSJsonOptions.Default, timeout.Token);
                replay.EnsureSuccessStatusCode();
                var replayed = await replay.Content.ReadFromJsonAsync<CertificateOperationDto>(RelaxKonOSJsonOptions.Default, timeout.Token);
                Require(replayed is { State: CertificateOperationState.Succeeded }
                    && replayed.OperationId == completed.OperationId && replayed.CertificateId == completed.CertificateId,
                    "replaying the signed ACME request returns the same successful operation and certificate");
                var reopened = new FileCertificateStore(builder.Environment, options,
                    new CertificateMetadataRepository(builder.Environment, builder.Configuration));
                var paths = await reopened.GetNginxPathsAsync(completed.CertificateId ?? throw new InvalidOperationException("Missing certificate ID."), timeout.Token)
                    ?? throw new InvalidOperationException("The issued certificate has no persisted deployment chain.");
                var bundle = new X509Certificate2Collection();
                bundle.ImportFromPemFile(paths.FullChainPath);
                try
                {
                    using var rootCertificate = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(Path.Combine(fixture, "issuance-root.pem"), timeout.Token));
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(rootCertificate);
                    chain.ChainPolicy.ExtraStore.AddRange(bundle);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    chain.ChainPolicy.DisableCertificateDownloads = true;
                    Require(bundle.Count >= 2 && bundle[0].MatchesHostname(domain) && chain.Build(bundle[0]),
                        "the reopened certificate store exposes a deployment chain that validates against the isolated Pebble root");
                }
                finally { foreach (var certificate in bundle) certificate.Dispose(); }
            }
            finally { await host.StopAsync(); }

            var (restartBuilder, restartModules) = ConfigureHost();
            await using var restarted = restartBuilder.Build();
            restarted.UseAuthentication();
            restarted.UseAuthorization();
            restartModules.MapEndpoints(restarted, ServerModuleContext.Create(restartBuilder.Configuration, restartBuilder.Environment, "memory"));
            restarted.Urls.Add("http://127.0.0.1:0");
            await restarted.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(restarted.Urls.Single()) };
                client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Admin);
                client.DefaultRequestHeaders.Add("Idempotency-Key", "signed-acme-issuance");
                var status = CertificateApiRoutes.Operations.Replace("{operationId}", completed.OperationId.ToString("D"));
                var recovered = await client.GetFromJsonAsync<CertificateOperationDto>(status, RelaxKonOSJsonOptions.Default);
                var request = new RequestCertificateRequest(["localhost"], CertificateChallengeType.DirectHttp01,
                    "acceptance@example.test", true, PublicReachabilityConfirmed: true);
                using var replay = await client.PostAsJsonAsync(CertificateApiRoutes.Request, request, RelaxKonOSJsonOptions.Default);
                replay.EnsureSuccessStatusCode();
                var replayed = await replay.Content.ReadFromJsonAsync<CertificateOperationDto>(RelaxKonOSJsonOptions.Default);
                Require(recovered is { State: CertificateOperationState.Succeeded }
                    && recovered.OperationId == completed.OperationId && recovered.CertificateId == completed.CertificateId
                    && replayed == recovered,
                    "after restart the production certificate API recovers and replays the original issuance receipt");
                var certificatePath = CertificateApiRoutes.ById.Replace("{id}", completed.CertificateId!.Value.ToString("D"));
                var certificate = await client.GetFromJsonAsync<CertificateDto>(certificatePath, RelaxKonOSJsonOptions.Default);
                var inventory = await client.GetFromJsonAsync<CertificateDto[]>(CertificateApiRoutes.Certificates, RelaxKonOSJsonOptions.Default);
                Require(certificate is { PrimaryDomain: "localhost", Status: CertificateStatus.Active, Thumbprint.Length: > 0 }
                    && certificate.Id == completed.CertificateId
                    && inventory is [var listed] && listed.Id == certificate.Id && listed.Thumbprint == certificate.Thumbprint,
                    "after restart certificate detail and inventory expose the same persisted ACME certificate");
            }
            finally { await restarted.StopAsync(); }

            return;
        }
        var acme = host.Services.GetRequiredService<IAcmeService>();
        var directory = new Uri("https://fixture.invalid/dir");
        var identity = new AcmeOrderIdentity(directory, new(directory, "/account"), "fixture", new(directory, "/order"));
        Require(!ReferenceEquals(execution, AcmeExecution.Shared)
            && await acme.ObserveOrderAsync(identity, default) == "unavailable" && execution.CurrentCode is { IsAlive: true },
            "the joint-installed signed Certificates package runs the production ACME observation executor");
    }

    public static void RunSignedDocker(string archive) => RunWithDispatcher(() => RunSignedExecutionAsync(Path.GetFullPath(archive),
        ServerModules.DockerId, ServerCapabilities.Docker, "Docker", DockerComposeExecution.AssemblyFile,
        DockerComposeExecution.EntryType, null, VerifySignedDockerAsync));

    private static async Task VerifySignedDockerAsync(TestHost _, string root)
    {
        var serverRoot = Path.Combine(root, "server");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = serverRoot });
        builder.Services.AddSingleton<IImageMirrorRepository, InMemoryImageMirrorRepository>();
        builder.Services.AddSingleton<IPrivilegedOperationTransport, DisabledPrivilegedOperationTransport>();
        builder.Services.AddSingleton<DockerStackOperationStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.EventAlerts.IOperationalEventPublisher, NoOperationalEvents>();
        builder.Services.AddSingleton(DispatchProxy.Create<IDockerEngineService, GitRepositoryAssemblyChecks.RejectTransport>());
        var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.DockerId }, InstalledModuleCatalog.Load(serverRoot));
        modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
        await using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        var compose = scope.ServiceProvider.GetRequiredService<IDockerComposeService>();
        var preview = await compose.PreviewAsync(new("joint-docker", "services:\n  web:\n    image: nginx:alpine\n"));
        Require(preview.Services.Single().Service == "web",
            "the joint-installed Docker execution package parses a Compose preview without contacting Docker Engine");
        if (Environment.GetEnvironmentVariable("RK_TEST_DOCKER_ENGINE") == "1")
            await ModulePackageHttpChecks.VerifyLiveComposeAsync(compose,
                host.Services.GetRequiredService<DockerStackOperationCoordinator>(),
                host.Services.GetRequiredService<DockerStackOperationStore>(), Require);
    }

    public static void RunSignedWebServer(string archive) => RunWithDispatcher(() => RunSignedExecutionAsync(Path.GetFullPath(archive),
        ServerModules.WebServerId, ServerCapabilities.WebServer, "WebServer", WebServerExecutionCode.AssemblyFile,
        WebServerExecutionCode.EntryType, null, VerifySignedWebServerAsync,
        [new(ServerModules.CertificatesId, "0.0.0", "9999.0.0")]));

    private static async Task VerifySignedWebServerAsync(TestHost _, string root)
    {
        var serverRoot = Path.Combine(root, "server");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = serverRoot });
        var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.WebServerId, ServerModules.CertificatesId },
            InstalledModuleCatalog.Load(serverRoot));
        modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
        await using var host = builder.Build();
        var execution = host.Services.GetRequiredService<WebServerExecutionCode>();
        var probe = new ModulePackageHttpChecks.WebServerReloadProbe();
        using var lease = execution.Acquire<INginxReloadExecutor>("RelaxKonOS.Server.WebServer.NginxReloadExecutor", probe);
        Require(!ReferenceEquals(execution, WebServerExecutionCode.Shared)
            && ReferenceEquals(host.Services.GetRequiredService<AcmeExecution>(), AcmeExecution.Shared)
            && await lease.Executor.ReloadAsync(default) == "" && probe.Confirmed,
            "the joint-installed signed Web Server executor uses its reload port and reuses bundled Certificates");
        if (Environment.GetEnvironmentVariable("RK_TEST_NGINX") is { Length: > 0 } executable)
            await VerifyLiveNginxReloadAsync(execution, Path.GetFullPath(executable), Directory.CreateDirectory(Path.Combine(root, "nginx")).FullName);
    }

    private static async Task VerifyLiveNginxReloadAsync(WebServerExecutionCode execution, string executable, string root)
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var config = Path.Combine(root, "nginx.conf");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = budget.Token;
        await WriteConfigAsync("before");
        using var nginx = Start("-g", "daemon off;");
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await WaitForResponseAsync("before");
            await WriteConfigAsync("after");
            var ports = new ModulePackageHttpChecks.WebServerReloadProbe(RunReloadAsync);
            using var lease = execution.Acquire<INginxReloadExecutor>("RelaxKonOS.Server.WebServer.NginxReloadExecutor", ports);
            Require(await lease.Executor.ReloadAsync(ct) == "" && ports.Confirmed, "the signed executor confirms a real Nginx reload");
            await WaitForResponseAsync("after");
            Require(!nginx.HasExited, "the original loopback Nginx master serves the new response after reload");

            await File.WriteAllTextAsync(config, "invalid_directive_for_acceptance;", ct);
            var failedPorts = new ModulePackageHttpChecks.WebServerReloadProbe(RunReloadAsync);
            using var failedLease = execution.Acquire<INginxReloadExecutor>("RelaxKonOS.Server.WebServer.NginxReloadExecutor", failedPorts);
            Require(await failedLease.Executor.ReloadAsync(ct) == NginxReloadOutcome.RecoveryRequired && !failedPorts.Confirmed,
                "the installed signed executor requires recovery when real Nginx rejects a candidate");
            Require(await client.GetStringAsync("/", ct) == "after" && !nginx.HasExited,
                "rejected configuration leaves the existing Nginx master serving the last valid response");

            await WriteConfigAsync("recovered");
            var recoveryPorts = new ModulePackageHttpChecks.WebServerReloadProbe(RunReloadAsync);
            using var recoveryLease = execution.Acquire<INginxReloadExecutor>("RelaxKonOS.Server.WebServer.NginxReloadExecutor", recoveryPorts);
            Require(await recoveryLease.Executor.ReloadAsync(ct) == "" && recoveryPorts.Confirmed,
                "the installed signed executor confirms a corrected Nginx configuration after rejection");
            await WaitForResponseAsync("recovered");
            Require(!nginx.HasExited, "the same Nginx master serves the corrected configuration");

            async Task<bool> RunReloadAsync(CancellationToken token)
            {
                using var reload = Start("-s", "reload");
                await reload.WaitForExitAsync(token);
                return reload.ExitCode == 0;
            }

            async Task WaitForResponseAsync(string expected)
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (nginx.HasExited) throw new InvalidOperationException($"Nginx exited with code {nginx.ExitCode}.");
                    try { if (await client.GetStringAsync("/", ct) == expected) return; }
                    catch (HttpRequestException) { }
                    await Task.Delay(100, ct);
                }
            }
        }
        finally
        {
            if (!nginx.HasExited) nginx.Kill(entireProcessTree: true);
            await nginx.WaitForExitAsync();
        }

        Task WriteConfigAsync(string response) => File.WriteAllTextAsync(config,
            $"pid nginx.pid; error_log error.log; events {{}} http {{ access_log off; client_body_temp_path client; proxy_temp_path proxy; fastcgi_temp_path fastcgi; uwsgi_temp_path uwsgi; scgi_temp_path scgi; server {{ listen 127.0.0.1:{port}; return 200 '{response}'; }} }}", ct);

        System.Diagnostics.Process Start(params string[] arguments)
        {
            var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (var argument in new[] { "-p", root + Path.DirectorySeparatorChar, "-c", config }.Concat(arguments))
                start.ArgumentList.Add(argument);
            return System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Nginx did not start.");
        }
    }

    public static void RunSignedFirewall(string archive) => RunWithDispatcher(() => RunSignedExecutionAsync(Path.GetFullPath(archive),
        ServerModules.FirewallId, ServerCapabilities.Firewall, "Firewall", CollectibleFirewallExecution.AssemblyFile,
        CollectibleFirewallExecution.EntryType, null, VerifySignedFirewallAsync));

    private static async Task VerifySignedFirewallAsync(TestHost host, string _)
    {
        const string route = "/api/v1.0/firewall/status";
        using var client = new HttpClient { BaseAddress = new Uri(host.Url) };
        var fixture = host.Services.GetRequiredService<IPrivilegedOperationTransport>() as FirewallResidencyChecks.FirewallTransport;
        var callsBeforeRead = fixture?.Calls;
        using var anonymous = await client.GetAsync(route);
        Require(anonymous.StatusCode == System.Net.HttpStatusCode.Unauthorized && (fixture is null || fixture.Calls == callsBeforeRead),
            $"anonymous firewall reads cannot reach the Helper transport ({anonymous.StatusCode}, calls {callsBeforeRead} to {fixture?.Calls})");
        client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Admin);
        var status = await client.GetFromJsonAsync<FirewallStatusDto>(route, RelaxKonOSJsonOptions.Default);
        Require(status is { IsAvailable: true } && (fixture is null || status.IsEnabled && fixture.Calls == callsBeforeRead + 1),
            "the joint-installed signed Firewall module returns status through the production route and Helper adapter");
        var callsAfterRead = fixture?.Calls;
        using var denied = await client.PutAsJsonAsync("/api/v1.0/firewall/enabled",
            new UpdateFirewallEnabledRequest(!status!.IsEnabled), RelaxKonOSJsonOptions.Default);
        denied.EnsureSuccessStatusCode();
        var result = await denied.Content.ReadFromJsonAsync<FirewallOperationResult>(RelaxKonOSJsonOptions.Default);
        Require(result is { Success: false, ProblemCode: "firewall.elevation_required" }
            && (fixture is null || fixture.Calls == callsAfterRead),
            "the signed Firewall route declines a change without host firewall elevation");
    }

    public static void RunSignedProxy(string archive) => RunWithDispatcher(() => RunSignedExecutionAsync(Path.GetFullPath(archive),
        ServerModules.ProxyId, ServerCapabilities.Proxy, "Proxy", ProxyExecutionLoadContext.AssemblyFile,
        ProxyConnectionExecutionGate.OverviewEntryType, null, VerifySignedProxyAsync,
        additionalCapabilities: [ServerCapabilities.GatewayProxy, ServerCapabilities.ProxyServer]));

    private static async Task VerifySignedProxyAsync(TestHost _, string root)
    {
        var serverRoot = Path.Combine(root, "server");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = serverRoot });
        var helper = new SquidPublicationHelper();
        builder.Services.AddSingleton<IPrivilegedOperationTransport>(helper);
        builder.Services.AddSingleton<IProxyPlatformPaths>(new SquidPublicationPaths(Path.Combine(root, "squid")));
        var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.ProxyId }, InstalledModuleCatalog.Load(serverRoot));
        modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
        await using var host = builder.Build();
        var execution = host.Services.GetRequiredService<ProxyConnectionExecutionGate>();
        await using var squidScope = host.Services.CreateAsyncScope();
        var squid = squidScope.ServiceProvider.GetRequiredService<SquidGatewayService>();
        var store = squidScope.ServiceProvider.GetRequiredService<SquidConfigurationStore>();
        var configuration = new SquidConfiguration();
        var renderedConfiguration = SquidConfigurationRenderer.Preview(configuration).Configuration!;
        using var lateFatal = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 10238) + "FATAL: missing resource"));
        using var lateFatalReader = new StreamReader(lateFatal);
        Require(await modulehelper::RelaxKonOS.PrivilegedHelper.SquidOperations.ReadBoundedAsync(lateFatalReader)
            is { Fatal: true, Text.Length: 8192 }, "the Helper detects Squid fatal diagnostics beyond its output limit");
        if (Environment.GetEnvironmentVariable("RK_TEST_SQUID") is { Length: > 0 } squidExecutable)
        {
            var configPath = Path.Combine(root, "squid-native.conf");
            var localPaths = "";
            if (Environment.GetEnvironmentVariable("RK_TEST_SQUID_ROOT") is { Length: > 0 } squidRoot)
            {
                var extracted = Path.GetFullPath(squidRoot);
                if (extracted.Any(char.IsWhiteSpace)) throw new InvalidOperationException("The extracted Squid path cannot contain whitespace.");
                localPaths = $"\nmime_table {extracted}/usr/share/squid/mime.conf\n"
                    + $"unlinkd_program {extracted}/usr/lib/squid/unlinkd\n"
                    + $"icon_directory {extracted}/usr/share/squid/icons\n";
            }
            await File.WriteAllTextAsync(configPath, renderedConfiguration + localPaths);
            var command = new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(squidExecutable))
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                ArgumentList = { "-k", "parse", "-f", configPath },
            };
            using var process = System.Diagnostics.Process.Start(command)
                ?? throw new InvalidOperationException("Could not start the Squid parser.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                var diagnostics = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var output = await diagnostics;
                using var diagnosticStream = new MemoryStream(Encoding.UTF8.GetBytes(output));
                using var diagnosticReader = new StreamReader(diagnosticStream);
                var diagnosticsResult = await modulehelper::RelaxKonOS.PrivilegedHelper.SquidOperations.ReadBoundedAsync(diagnosticReader);
                if (process.ExitCode != 0 || diagnosticsResult.Fatal)
                    throw new InvalidOperationException("Squid rejected the rendered publication candidate: " + output);
                Console.WriteLine("PASS: the real Squid parser accepts the rendered publication candidate");
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        var configurationDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(renderedConfiguration)));
        var operationId = Guid.NewGuid();
        var receipt = new SquidPublicationReceipt(operationId, configurationDigest, configurationDigest, true);
        helper.Receipt = receipt with { ObservedHash = new string('a', 64) };
        Require(!ReferenceEquals(squid.ReceiptExecution, SquidGatewayService.BundledReceiptExecution)
            && await squid.ApplyAsync(operationId, new(configuration), _ => Task.CompletedTask, default) == "squid.recovery.required"
            && await store.ReadAsync(default) is { DesiredRevision: 1, PublishedRevision: null, LastProblemCode: "squid.publication.unconfirmed" },
            "the signed Squid executor leaves an inconsistent publication unconfirmed");
        helper.Receipt = receipt with { OperationId = Guid.NewGuid() };
        Require(await squid.RecoverPublicationAsync(operationId, default) is null,
            "the signed Squid executor rejects a recovery receipt for another operation");
        helper.Receipt = receipt;
        Require(await squid.RecoverPublicationAsync(operationId, default) == receipt
            && await store.ReadAsync(default) is { DesiredRevision: 1, PublishedRevision: 1, LastProblemCode: null } confirmed
            && confirmed.PublishedConfigurationHash == configurationDigest,
            "the signed Squid executor confirms the persisted revision only with the original receipt");
        var rollbackId = Guid.NewGuid();
        helper.Receipt = receipt with { OperationId = rollbackId, Applied = false };
        Require(await squid.ApplyAsync(rollbackId, new(configuration), _ => Task.CompletedTask, default) == SquidGatewayService.RolledBack
            && await store.ReadAsync(default) is { DesiredRevision: 2, PublishedRevision: 1, LastProblemCode: SquidGatewayService.RolledBack },
            "a verified rollback keeps the last published Squid revision");
        helper.LifecycleReceipt = new(operationId, SquidServiceAction.Start);
        Require(!await squid.RecoverLifecycleAsync(operationId, SquidServiceAction.Reload)
            && await squid.RecoverLifecycleAsync(operationId, SquidServiceAction.Start),
            "the signed Squid verifier confirms only the original lifecycle action");
        helper.AccountReceipt = new(operationId, "original-request", null);
        Require(await squid.RecoverAccountAsync(operationId, "another-request") is null
            && await squid.RecoverAccountAsync(operationId, "original-request") == helper.AccountReceipt,
            "the signed Squid verifier confirms only the original account request");
        using var overview = execution.AcquireOverview();
        var reads = new ProxyOverviewReads(_ => Task.CompletedTask, _ => Task.FromResult(false), _ => Task.FromResult<bool?>(true),
            _ => throw new InvalidOperationException("Runtime observation must take precedence."), _ => Task.FromResult(true), _ => Task.FromResult(3));
        Require(!ReferenceEquals(execution, ProxyConnectionExecutionGate.Shared)
            && await overview.ReadAsync(reads, default) is { TunEnabled: true, ActiveConnections: 3 },
            "the joint-installed signed Proxy package computes an overview from host observations");

        var installed = InstalledModuleStore.Read(serverRoot).Single(record => record.ModuleId == ServerModules.ProxyId);
        var manifest = InstalledModuleStore.VerifyPackage(serverRoot, installed);
        var packageRoot = InstalledModuleStore.ResolvePackageRoot(serverRoot, installed.PackagePath);
        var subscriptions = new ProxyExecutionCodeGate("joint-subscription", () => Path.Combine(packageRoot, manifest.EntryAssembly));
        using (var missingCode = subscriptions.Acquire())
        {
            const string missingEntry = "RelaxKonOS.Server.Proxy.MissingAcceptanceEntry";
            try
            {
                missingCode.Create<IProxySubscriptionNormalizer>(missingEntry);
                throw new InvalidOperationException("A missing signed execution entry was accepted.");
            }
            catch (InvalidDataException error)
            {
                Require(error.Message == $"Proxy execution entry '{missingEntry}' could not be loaded." && error.InnerException is null,
                    "a signed Proxy entry load failure identifies the entry without retaining its execution context");
            }
            try
            {
                missingCode.RequireConstructor<IProxyQueuedSubscriptionExecutor>(ProxySubscriptionOperations.EntryType, typeof(string));
                throw new InvalidOperationException("An incompatible signed subscription executor constructor was accepted.");
            }
            catch (InvalidDataException error)
            {
                Require(error.Message.Contains("does not provide the required constructor", StringComparison.Ordinal),
                    "the installed Proxy entry gate rejects an incompatible queued subscription constructor");
            }
        }
        const string yaml = "proxies: []\nrules: [MATCH,DIRECT]\n";
        var encoded = System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(yaml)));
        var normalized = await subscriptions.ReadAsync(new MemoryStream(encoded), default);
        var invalid = await subscriptions.ReadAsync(new MemoryStream([0xff]), default);
        Require(normalized.Content == yaml && normalized.Bytes == encoded.Length && invalid.Content is null,
            "the joint-installed signed Proxy parser decodes an authorized subscription body and rejects malformed UTF-8");
        var subscriptionHttp = new ProxySubscriptionHttpFixture(encoded);
        var downloader = new ProxySubscriptionDownloader(subscriptionHttp, subscriptionHttp) { Execution = subscriptions };
        try
        {
            await downloader.DownloadAsync("https://127.0.0.1/subscription", ProxySubscriptionDownloadRoute.Direct, default);
            throw new InvalidOperationException("The downloader accepted a loopback subscription source.");
        }
        catch (ProxySubscriptionException error) when (error.ProblemCode == ProxyProblemCodes.SubscriptionInvalid)
        {
            Require(subscriptionHttp.RequestCount == 0, "the subscription downloader rejects loopback before sending HTTP");
        }
        var downloaded = await downloader.DownloadAsync("https://1.1.1.1/subscription", ProxySubscriptionDownloadRoute.Direct, default);
        Require(downloaded.Content == yaml && subscriptionHttp.RequestCount == 1,
            "the production downloader normalizes a permitted response through the installed signed Proxy parser");
        if (Environment.GetEnvironmentVariable("RK_TEST_MIHOMO_CONTROLLER") is { Length: > 0 } endpoint)
        {
            var expectedConnections = int.Parse(Environment.GetEnvironmentVariable("RK_TEST_MIHOMO_CONNECTIONS") ?? "0",
                System.Globalization.CultureInfo.InvariantCulture);
            var secrets = new DataProtectionProxyControllerSecretStore(builder.Environment,
                new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
            using var controller = new MihomoControllerClient(new HttpClient(new SocketsHttpHandler { UseProxy = false }),
                secrets, new MihomoControllerOptions { Endpoint = new Uri(endpoint) }) { ConnectionExecution = execution };
            var observed = await overview.ReadAsync(new ProxyOverviewReads(_ => Task.CompletedTask, _ => Task.FromResult(false),
                _ => Task.FromResult<bool?>(null),
                async ct =>
                {
                    var reachable = await controller.IsReachableAsync(ct);
                    Require(reachable is { Succeeded: true, Value: true }, "the production Mihomo adapter reaches the real loopback controller");
                    return reachable.Value;
                },
                async ct =>
                {
                    var tun = await controller.GetTunEnabledAsync(ct);
                    Require(tun is { Succeeded: true, Value: false }, "the production Mihomo adapter reads the disabled TUN state");
                    return tun.Value;
                },
                async ct =>
                {
                    var count = await controller.GetConnectionCountAsync(ct);
                    Require(count.Succeeded && count.Value == expectedConnections, "the signed connection reader counts the real Mihomo connections");
                    return count.Value;
                }), default);
            Require(!observed.TunEnabled && observed.ActiveConnections == expectedConnections,
                "the joint-installed signed Proxy executor reports the real Mihomo overview");
            if (Environment.GetEnvironmentVariable("RK_TEST_MIHOMO_CONFIG") is { Length: > 0 } config)
                await VerifySignedMihomoPublicationAsync(controller, Path.GetFullPath(config), Path.Combine(packageRoot, manifest.EntryAssembly));
        }
    }

    private static async Task VerifySignedMihomoPublicationAsync(MihomoControllerClient controller, string path, string entryAssembly)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var original = await File.ReadAllTextAsync(path, ct);
        const string initialSetting = "log-level: info";
        Require(original.Split('\n').Contains(initialSetting), "the isolated Mihomo fixture starts with the expected configuration");
        await File.WriteAllTextAsync(path, original.Replace(initialSetting, "log-level: warning", StringComparison.Ordinal), ct);
        try
        {
            using var execution = MihomoDirectPublicationCode.Acquire(new ProxyExecutionCodeGate("joint-publication", () => entryAssembly));
            var confirmed = false;
            var result = await execution.PublishProfileAsync(new(
                () => controller.ReloadAsync(ct),
                async () => await controller.GetListenerStateAsync(ct) is { Succeeded: true, Value.LogLevel: "warning" },
                () => true,
                Restore,
                () => throw new InvalidOperationException("The fixture always has a previous configuration."),
                Restore,
                () => Task.FromResult(confirmed = true),
                () => Task.CompletedTask,
                _ => { }));
            Require(result is null && confirmed, "the installed signed publication executor reloads real Mihomo and confirms the observed change");

            var lastValid = await File.ReadAllTextAsync(path, ct);
            await File.WriteAllTextAsync(path, lastValid.Replace("log-level: warning", "log-level: debug", StringComparison.Ordinal), ct);
            var observations = new List<string>();
            var completedCompensation = false;
            string? reportedCompensation = null;
            var failed = await execution.PublishProfileAsync(new(
                () => controller.ReloadAsync(ct),
                async () =>
                {
                    var state = await controller.GetListenerStateAsync(ct);
                    if (!state.Succeeded || state.Value is not { } listener) return false;
                    observations.Add(listener.LogLevel);
                    return listener.LogLevel == "warning";
                },
                () => true,
                () => throw new InvalidOperationException("The loopback reload must return a known result."),
                () => throw new InvalidOperationException("The fixture always has a previous configuration."),
                () => File.WriteAllText(path, lastValid),
                () => throw new InvalidOperationException("The failed candidate must not be confirmed."),
                () => { completedCompensation = true; return Task.CompletedTask; },
                problem => reportedCompensation = problem));
            Require(failed == ProxyProblemCodes.ConfigApplyFailed && reportedCompensation == failed
                && completedCompensation && observations is ["debug", "warning"],
                "the signed executor compensates after a failed health check and observes the previous real Mihomo configuration");
        }
        finally
        {
            Restore();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Require(await controller.ReloadAsync(cleanup.Token) is null
                && await controller.GetListenerStateAsync(cleanup.Token) is { Succeeded: true, Value.LogLevel: "info" },
                "the temporary Mihomo instance returns to its original configuration");
        }

        void Restore() => File.WriteAllText(path, original);
    }

    private static async Task RunSignedExecutionAsync(string archive, string moduleId, string capability,
        string directory, string entryAssembly, string entryType, IReadOnlyDictionary<string, string>? contractVersions,
        Func<TestHost, string, Task> verifyBusiness, IReadOnlyList<ModulePackageDependency>? dependencies = null,
        IReadOnlyList<string>? additionalCapabilities = null)
    {
        var parent = Path.Combine(AppContext.BaseDirectory, "test-data");
        var root = Directory.CreateDirectory(Path.Combine(parent, "signed-execution-joint-" + Guid.NewGuid().ToString("N"))).FullName;
        TestHost? host = null;
        try
        {
            var official = Directory.CreateDirectory(Path.Combine(root, "OfficialApps")).FullName;
            var appArchive = Path.Combine(official, Path.GetFileName(archive));
            const string version = "99.0.0";
            await RewriteDesktopArchiveAsync(archive, appArchive, manifest =>
                manifest["dependencies"]!["required"]![0]!["minimumVersion"] = version);
            var serverRoot = Directory.CreateDirectory(Path.Combine(root, "server")).FullName;
            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var key = Path.Combine(root, "signing.pem");
            await File.WriteAllTextAsync(key, signer.ExportPkcs8PrivateKeyPem());
            var now = DateTimeOffset.UtcNow;
            var rid = ModulePackagePlatform.CurrentRid;
            var executionDirectory = Path.Combine(AppContext.BaseDirectory, "Modules", directory);
            var package = await ModulePackageBuilder.BuildAsync(new(executionDirectory,
                Path.Combine(root, "module"), moduleId, version, "joint-execution-" + rid, "fixture", "official", rid,
                entryAssembly, entryType, "joint-execution", new string('a', 40), "official", 1,
                "https://modules.example/", key, "fixture-key", 3600, Capabilities: [capability, .. additionalCapabilities ?? []],
                Dependencies: dependencies, CapabilityContractVersions: contractVersions), now);
            ModulePackageBuildResult? incompatible = null;
            if (moduleId is ServerModules.ProxyId or ServerModules.FirewallId or "relaxkonos.tailscale")
            {
                var incompatibleSource = Directory.CreateDirectory(Path.Combine(root, "incompatible-execution")).FullName;
                var bytes = await File.ReadAllBytesAsync(Path.Combine(executionDirectory, entryAssembly));
                var name = bytes.AsSpan().IndexOf(moduleId switch
                {
                    ServerModules.ProxyId => "ProxyQueuedSubscriptionExecutor\0"u8,
                    ServerModules.FirewallId => "IFirewallExecutionModule\0"u8,
                    _ => "ITailscaleExecution\0"u8,
                });
                if (name < 0) throw new InvalidOperationException("The fixture could not find the incompatible execution type name.");
                bytes[name] = (byte)'X';
                await File.WriteAllBytesAsync(Path.Combine(incompatibleSource, entryAssembly), bytes);
                incompatible = await ModulePackageBuilder.BuildAsync(new(incompatibleSource,
                    Path.Combine(root, "incompatible-module"), moduleId, "98.0.0", "incompatible-execution-" + rid,
                    "fixture", "official", rid, entryAssembly, entryType, "joint-execution", new string('a', 40),
                    "official", 1, "https://modules.example/", key, "fixture-key", 3600,
                    Capabilities: [capability, .. additionalCapabilities ?? []]), now);
                await using var invalidArchive = File.OpenRead(incompatible.ArchivePath);
                try
                {
                    await InstalledModuleInstaller.InstallVerifiedAsync(serverRoot, "official", incompatible.Manifest,
                        incompatible.ManifestSha256, incompatible.ArchiveSha256, invalidArchive, now);
                    throw new InvalidOperationException($"The incompatible {moduleId} execution package was accepted.");
                }
                catch (ModulePackageException error) when (error.ProblemCode == "module-package-execution-contract")
                {
                    Require(InstalledModuleStore.Read(serverRoot).Count == 0
                        && !Directory.Exists(Path.Combine(serverRoot, "data", "module-management", "packages", moduleId, "98.0.0")),
                        $"an incompatible signed {moduleId} execution package is rejected before committing a version");
                }
            }
            var policy = new ModulePackageTrustPolicy(1, "official", 1, true, "fixture", "official", "https://modules.example/",
                [moduleId], new Dictionary<string, string> { ["fixture-key"] = package.PublicKeySpkiBase64 }, 3600);
            var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
            {
                ModuleManagement = new { Authority = "managed" },
                ModulePackages = new { Sources = new Dictionary<string, ModulePackageTrustPolicy> { ["official"] = policy } },
            }, RelaxKonOSJsonOptions.Default))).Build();
            string serviceId;
            using (var manager = new ModuleManagementStore(config, serverRoot)) serviceId = manager.ServiceId;
            using (var catalogs = new ModulePackageCatalogStore(serverRoot, serviceId))
                catalogs.Accept(await File.ReadAllBytesAsync(package.CatalogPath), policy,
                    new(serviceId, rid, ModulePackageHostConfiguration.HostApiVersion, false), ModulePackageCatalogStore.InitialRevision,
                    ModuleTestAuthentication.Admin, "joint-execution", now);

            host = await TestHost.StartAsync(serverRoot, config);
            var auth = DispatchProxy.Create<IAuthSession, SessionProxy>();
            var session = (SessionProxy)(object)auth;
            session.Url = host.Url;
            using var factory = new FixtureHttpFactory(await File.ReadAllBytesAsync(package.ArchivePath));
            var settings = new ShellSettings(null!);
            var localization = new LocalizationService(settings, new SshDesktopSession(null!, settings));
            using var services = new ServiceCollection().AddSingleton(localization).BuildServiceProvider();
            var windows = new RelaxKonOS.WindowManager.WindowManager();
            var apps = new ApplicationManager(windows, services);
            var drive = new VirtualSystemDrive(Path.Combine(root, "desktop"));
            var packages = new DeveloperPackageManager(apps, null!, windows, DispatchProxy.Create<IAppPermissionManager, PermissionProxy>(),
                new Diagnostics(), drive, official, moduleHttp: factory, moduleSession: auth);
            await packages.LoadInstalledAsync();
            using var trust = new ComponentCatalogSourceStore(Path.Combine(root, "sources"), "joint-execution");
            var installer = new AppPackageInstallerService(packages, null!, trust);
            var joint = new JointAppDeploymentService(factory, auth, installer, packages, apps, new(Path.Combine(root, "journal")));
            var candidate = await installer.InspectLocalAsync(appArchive);
            if (moduleId == ServerModules.ProxyId && Environment.GetEnvironmentVariable("RK_TEST_PROXY_SHARED_APPS") == "1")
            {
                var profilePath = Path.Combine(Path.GetDirectoryName(archive)!, "installation-profiles.json");
                var profiles = JsonNode.Parse(await File.ReadAllTextAsync(profilePath))!;
                var developer = profiles["profiles"]!["developer"]!.AsArray();
                developer.Single(entry => (string?)entry!["appId"] == "relaxkonos.git")!["minimumVersion"] = "99.0.0";
                await File.WriteAllTextAsync(packages.OfficialSource.BundledProfilesPath, profiles.ToJsonString());
                const string gitArchive = "relaxkonos.git.roapp";
                File.Copy(Path.Combine(Path.GetDirectoryName(archive)!, gitArchive), Path.Combine(official, gitArchive));
                File.Copy(Path.Combine(Path.GetDirectoryName(archive)!, gitArchive + ".sha256"), Path.Combine(official, gitArchive + ".sha256"));
                var exclusions = developer
                    .Where(entry => (string?)entry!["installPolicy"] == "offline")
                    .Select(entry => "--exclude-bundled-app=" + (string)entry!["appId"]!);
                var provisioner = new BundledApplicationProvisioner(packages, installer, joint, drive);
                await provisioner.RunAsync(["--installation-profile=developer", .. exclusions]);
                typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, services);
                using var store = new AppInstallerViewModel(installer) { JointDeployment = joint, ProfileChoices = provisioner };
                await store.RefreshOfficialCommand.ExecuteAsync(null);
                Require(store.SelectedOfficialApp is { SelectedByProfile: true } selected && selected.Id == candidate.Manifest.Id,
                    "the official store selects the Developer-profile Proxy archive for review");
                foreach (var unavailableId in new[] { "relaxkonos.git", "relaxkonos.docker" })
                {
                    store.SelectedOfficialApp = store.OfficialApps.Single(item => item.Id == unavailableId);
                    Require(store.SelectedOfficialApp is { SelectedByProfile: true, ProfileReleaseUnavailable: true }
                        && !store.DownloadOfficialCommand.CanExecute(null),
                        $"the Developer profile retains {unavailableId} but blocks staging an unavailable release");
                    await store.DownloadOfficialCommand.ExecuteAsync(null);
                    Require(store.CurrentPackage is null, $"the store does not stage {unavailableId} without a matching release");
                }
                store.SelectedOfficialApp = store.OfficialApps.Single(item => item.Id == candidate.Manifest.Id);
                await store.DownloadOfficialCommand.ExecuteAsync(null);
                var staged = store.CurrentPackage;
                DeploymentPlan? declined = null;
                store.SelectOptionalAsync = requirements => Task.FromResult<IReadOnlyList<DeploymentRequirement>?>(requirements);
                store.ConfirmDeploymentAsync = plan =>
                {
                    declined = plan;
                    return Task.FromResult(false);
                };
                string? reportedError = null;
                store.ShowMessageAsync = (_, message) =>
                {
                    reportedError = message;
                    return Task.CompletedTask;
                };
                factory.BeforeNextRequest = () => throw new HttpRequestException(HttpRequestError.ConnectionError, "fixture server unreachable");
                await store.InstallCommand.ExecuteAsync(null);
                Require(staged is not null && ReferenceEquals(store.CurrentPackage, staged) && declined is null
                    && reportedError?.Contains(RelaxKonOS.Client.Localization.LocalizedText.Get(
                        "app_installer.deployment.server_unreachable"), StringComparison.Ordinal) == true
                    && ModulePackageOperationStore.Read(serverRoot).Count == 0,
                    "an unreachable server leaves the Developer-profile archive ready to retry without deploying");
                await store.InstallCommand.ExecuteAsync(null);
                Require(declined is { PreparedSteps.Length: 1 }
                    && declined.OrderedReleases.Any(release => release.Package.Kind == DeploymentPackageKind.ServerModule
                        && release.Package.Id == moduleId && release.Version == version && !release.Satisfied)
                    && ModulePackageOperationStore.Read(serverRoot).Count == 0 && !apps.IsRegistered(new AppId(candidate.Manifest.Id)),
                    "declining the Developer-profile plan leaves the signed Proxy module and desktop app uninstalled");
            }
            var review = await joint.PrepareAsync(candidate, [], default);
            Require(review.Plan.PreparedSteps.Length == 1, $"the {moduleId} desktop package selects the independent signed server release");
            if (dependencies is { Count: > 0 })
                Require(dependencies.All(dependency => review.Plan.OrderedReleases.Any(release =>
                    release.Package == new DeploymentPackageKey(DeploymentPackageKind.ServerModule, dependency.ModuleId) && release.Satisfied)),
                    "the signed Web Server plan reuses its effective Certificates dependency");
            await ExpectRestartAsync(joint, review);
            Require(!apps.IsRegistered(new AppId(candidate.Manifest.Id)), $"the desktop waits for the signed {moduleId} package to become effective");
            await host.DisposeAsync();
            host = null;
            host = await TestHost.StartAsync(serverRoot, config);
            session.Url = host.Url;
            var resumed = await joint.FindPendingAsync(candidate, default) ?? throw new InvalidOperationException($"{moduleId} joint plan was not recoverable.");
            var installed = await joint.ExecuteAsync(resumed, default);
            Require(apps.IsRegistered(new AppId(installed.Id)) && InstalledModuleStore.Read(serverRoot).Single() is { Version: version }
                && ModulePackageOperationStore.Read(serverRoot).Count == 1,
                $"after restart the desktop registers {moduleId} against the signed execution package without resubmitting installation");
            if (incompatible is not null)
            {
                var active = InstalledModuleStore.Read(serverRoot).Single();
                var prior = active with
                {
                    Version = incompatible.Manifest.Version,
                    ReleaseId = incompatible.Manifest.ReleaseId,
                    PackagePath = $"packages/{moduleId}/{incompatible.Manifest.Version}",
                    ManifestSha256 = incompatible.ManifestSha256,
                    ArtifactSha256 = incompatible.ArchiveSha256,
                };
                var priorRoot = Path.Combine(serverRoot, "data", "module-management", prior.PackagePath);
                ZipFile.ExtractToDirectory(incompatible.ArchivePath, priorRoot);
                InstalledModuleStore.WriteInstallMetadata(priorRoot, prior);
                await using (var retryArchive = File.OpenRead(incompatible.ArchivePath))
                {
                    try
                    {
                        await InstalledModuleInstaller.InstallVerifiedAsync(serverRoot, prior.SourceId, incompatible.Manifest,
                            prior.ManifestSha256, prior.ArtifactSha256, retryArchive, now, activate: false);
                        throw new InvalidOperationException($"An incompatible staged {moduleId} package was reused.");
                    }
                    catch (ModulePackageException error) when (error.ProblemCode == "module-package-execution-contract")
                    {
                        Require(Directory.Exists(priorRoot) && InstalledModuleStore.Read(serverRoot).Single().ArtifactSha256 == active.ArtifactSha256,
                            $"a staged reuse rejects incompatible {moduleId} code without changing either immutable version");
                    }
                }
                try
                {
                    await InstalledModuleInstaller.RollbackAsync(serverRoot, config, moduleId, prior.Version);
                    throw new InvalidOperationException($"Rollback accepted the incompatible {moduleId} execution package.");
                }
                catch (ModulePackageException error) when (error.ProblemCode == "module-package-execution-contract")
                {
                    Require(InstalledModuleStore.Read(serverRoot).Single().ArtifactSha256 == active.ArtifactSha256,
                        $"an incompatible historical {moduleId} version cannot displace the active version during rollback");
                }
            }
            await verifyBusiness(host, root);
            var verifySharedProxy = moduleId == ServerModules.ProxyId && Environment.GetEnvironmentVariable("RK_TEST_PROXY_SHARED_APPS") == "1";
            if (verifySharedProxy)
            {
                string[] siblingIds = ["relaxkonos.proxy-server", "relaxkonos.squid"];
                var sourceDirectory = Path.GetDirectoryName(archive)!;
                var keyForProxy = new DeploymentPackageKey(DeploymentPackageKind.ServerModule, moduleId);
                using var readinessServices = new ServiceCollection().AddSingleton(packages).AddSingleton(apps)
                    .AddSingleton<RelaxKonOS.WindowManager.IWindowManager>(windows)
                    .AddSingleton<IHttpClientFactory>(factory).BuildServiceProvider();
                using var readiness = new ApplicationAvailabilityService(auth, readinessServices, null!);
                foreach (var siblingId in siblingIds)
                {
                    var filename = siblingId + ".roapp";
                    var path = Path.Combine(official, filename);
                    File.Copy(Path.Combine(sourceDirectory, filename), path);
                    File.Copy(Path.Combine(sourceDirectory, filename + ".sha256"), path + ".sha256");
                    var sibling = await installer.InspectLocalAsync(path);
                    var siblingReview = await joint.PrepareAsync(sibling, [], default);
                    Require(siblingReview.Plan.PreparedSteps.IsEmpty && siblingReview.Plan.OrderedReleases.Any(
                        release => release.Package == keyForProxy && release.Satisfied && release.Version == version),
                        $"{siblingId} reuses the active signed Proxy version");
                    var siblingInstalled = await joint.ExecuteAsync(siblingReview, default);
                    Require(siblingInstalled.Id == siblingId && apps.IsRegistered(new AppId(siblingId)),
                        $"{siblingId} registers without another module installation");
                    var manifest = apps.GetManifest(new AppId(siblingId))!;
                    var availability = await readiness.RefreshAsync(manifest);
                    Require(availability is { CanOpen: true, PackageKind: DeploymentPackageKind.ServerModule }
                        or { State: ComponentAvailabilityKind.NoPermission, PackageKind: null },
                        $"{siblingId} launch readiness accepts the shared signed Proxy module");
                }
                var dependents = packages.RequiredBy(keyForProxy);
                Require(dependents.ToHashSet(StringComparer.Ordinal).SetEquals([installed.Id, .. siblingIds]),
                    "the three Proxy applications share the same signed server dependency");
                Require(await IsDesktopDependencyProtectedAsync(), "the server blocks a shared Proxy disable while all three apps are installed");
                foreach (var siblingId in siblingIds)
                {
                    Require(await packages.UninstallAsync(siblingId) && !packages.RequiredBy(keyForProxy).Contains(siblingId)
                        && packages.RequiredBy(keyForProxy).Contains(installed.Id),
                        $"uninstalling {siblingId} preserves the remaining Proxy Manager dependency");
                    Require(await IsDesktopDependencyProtectedAsync(), "the server still blocks Proxy disable after a dependent app is removed");
                }
                Require(ModulePackageOperationStore.Read(serverRoot).Count == 1
                    && InstalledModuleStore.Read(serverRoot).Single().ArtifactSha256 == package.ArchiveSha256,
                    "sharing the Proxy module leaves the single installed signed archive intact");
            }
            if (moduleId == "relaxkonos.tunnels")
            {
                var protectionHttp = factory.CreateClient("joint-fixture");
                var management = new ServerModuleManagementClient(protectionHttp, auth);
                var inventory = await management.ReadAsync();
                var desired = inventory.Where(module => module.DesiredEnabled && module.Id != moduleId)
                    .Select(module => module.Id).Order(StringComparer.Ordinal).ToArray();
                var disable = await management.PreviewAsync(new(serviceId, ServerModuleAction.Disable, [moduleId],
                    desired, inventory[0].DesiredRevision!, "protect-signed-tunnels"));
                Require(disable.BlockedBy.Any(blocker => blocker.ModuleId == moduleId
                    && blocker.ProblemCode == "module-desktop-dependency-blocked"),
                    "the server blocks disabling signed Tunnels while Tunnel Manager depends on it");
            }
            Require(await packages.UninstallAsync(installed.Id) && !apps.IsRegistered(new AppId(installed.Id))
                && InstalledModuleStore.Read(serverRoot).Single().ArtifactSha256 == package.ArchiveSha256,
                $"uninstalling the desktop {moduleId} package releases the app without removing the server module");
            if (verifySharedProxy)
                Require(!await IsDesktopDependencyProtectedAsync(), "removing the final Proxy dependent clears the server-side desktop blocker");

            async Task<bool> IsDesktopDependencyProtectedAsync()
            {
                var management = new ServerModuleManagementClient(factory.CreateClient("joint-fixture"), auth);
                var inventory = await management.ReadAsync();
                var target = inventory.Single(module => module.Id == moduleId);
                var desired = inventory.Where(module => module.DesiredEnabled && module.Id != moduleId)
                    .Select(module => module.Id).Order(StringComparer.Ordinal).ToArray();
                var plan = await management.PreviewAsync(new(serviceId, ServerModuleAction.Disable, [moduleId],
                    desired, target.DesiredRevision!, Guid.NewGuid().ToString("N")));
                return plan.BlockedBy.Any(blocker => blocker.ModuleId == moduleId
                    && blocker.ProblemCode == "module-desktop-dependency-blocked");
            }
            if (moduleId == "relaxkonos.tunnels")
            {
                using var managementHttp = factory.CreateClient("joint-fixture");
                var management = new ServerModuleManagementClient(managementHttp, auth);
                var inventory = await management.ReadAsync();
                var target = inventory.Single(module => module.Id == moduleId);
                var desired = inventory.Where(module => module.DesiredEnabled && module.Id != moduleId)
                    .Select(module => module.Id).Order(StringComparer.Ordinal).ToArray();
                var disable = await management.PreviewAsync(new(serviceId, ServerModuleAction.Disable, [moduleId],
                    desired, target.DesiredRevision!, "disable-signed-tunnels"));
                Require(disable.BlockedBy.Count == 0, "uninstalling Tunnel Manager releases the server-side module dependency");
                var receipt = await management.ApplyAsync(disable.PlanId);
                Require(receipt.State == ServerModuleOperationState.AwaitingRestart, "signed Tunnels disable waits for restart");
                await host.DisposeAsync();
                host = null;
                host = await TestHost.StartAsync(serverRoot, config);
                session.Url = host.Url;
                var restartedManagement = new ServerModuleManagementClient(managementHttp, auth);
                var restartedInventory = await restartedManagement.ReadAsync();
                var restartedTarget = restartedInventory.Single(module => module.Id == moduleId);
                var completed = await restartedManagement.OperationAsync(receipt.OperationId);
                using var client = new HttpClient { BaseAddress = new Uri(host.Url) };
                client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Admin);
                using var routes = await client.GetAsync(TunnelApiRoutes.Profiles);
                Require(!restartedTarget.EffectiveEnabled && completed.State == ServerModuleOperationState.Succeeded
                    && routes.StatusCode == System.Net.HttpStatusCode.NotFound
                    && await client.GetStringAsync("/fixture-core") == "ready"
                    && InstalledModuleStore.Read(serverRoot).Single().ArtifactSha256 == package.ArchiveSha256,
                    "disabling signed Tunnels withdraws its routes and leaves Core and the installed package available");

                var enable = await restartedManagement.PreviewAsync(new(serviceId, ServerModuleAction.Enable, [moduleId],
                    [.. desired, moduleId], restartedTarget.DesiredRevision!, "reenable-signed-tunnels"));
                Require(enable.BlockedBy.Count == 0, "the existing signed Tunnels version can be enabled again");
                var enableReceipt = await restartedManagement.ApplyAsync(enable.PlanId);
                Require(enableReceipt.State == ServerModuleOperationState.AwaitingRestart, "Tunnels re-enable waits for restart");
                await host.DisposeAsync();
                host = null;
                host = await TestHost.StartAsync(serverRoot, config);
                session.Url = host.Url;
                var enabledManagement = new ServerModuleManagementClient(managementHttp, auth);
                var enabledInventory = await enabledManagement.ReadAsync();
                var enabledReceipt = await enabledManagement.OperationAsync(enableReceipt.OperationId);
                using var restoredClient = new HttpClient { BaseAddress = new Uri(host.Url) };
                restoredClient.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Admin);
                var restoredProfiles = await restoredClient.GetFromJsonAsync<TunnelServerProfileDto[]>(TunnelApiRoutes.Profiles, RelaxKonOSJsonOptions.Default);
                Require(enabledInventory.Single(module => module.Id == moduleId).EffectiveEnabled
                    && enabledReceipt.State == ServerModuleOperationState.Succeeded
                    && restoredProfiles?.Any(profile => profile.Name == "edge") == true
                    && ModulePackageOperationStore.Read(serverRoot).Count == 1,
                    "re-enabling signed Tunnels restores its persisted profiles without reinstalling the package");
            }
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            if (Path.GetDirectoryName(root) == parent)
            {
                // Windows can retain loaded application assemblies until this process exits.
                try { Directory.Delete(root, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { Console.WriteLine($"Fixture cleanup deferred until process exit: {root}"); }
            }
        }
    }

    private static async Task VerifySignedGitAsync(TestHost host, string root)
    {
        using var businessClient = new HttpClient { BaseAddress = new Uri(host.Url) };
        businessClient.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Admin);
        var engine = await businessClient.GetFromJsonAsync<GitEngineStatusDto>(GitApiRoutes.EngineStatus, RelaxKonOSJsonOptions.Default);
        Require(engine is { IsAvailable: true, Version.Length: > 0 },
            "the Git endpoint invokes the real local Git CLI through the installed signed execution package");
        var worktree = Directory.CreateDirectory(Path.Combine(root, "checkout")).FullName;
        using var initialized = await businessClient.PostAsync(GitApiRoutes.Init + "?path=" + Uri.EscapeDataString(worktree), null);
        initialized.EnsureSuccessStatusCode();
        Require((await initialized.Content.ReadFromJsonAsync<GitOperationResult>(RelaxKonOSJsonOptions.Default))?.Success == true,
            "the installed Git package initializes a real temporary worktree through HTTP");
        using var registration = await businessClient.PostAsJsonAsync(GitApiRoutes.Repositories,
            new GitRepositoryRegistration("Signed Git", worktree), RelaxKonOSJsonOptions.Default);
        registration.EnsureSuccessStatusCode();
        var repository = (await registration.Content.ReadFromJsonAsync<GitRepositoryDto>(RelaxKonOSJsonOptions.Default))!;
        await File.WriteAllTextAsync(Path.Combine(worktree, "joint.txt"), "signed Git deployment");
        using var staged = await businessClient.PostAsJsonAsync(GitApiRoutes.Stage.Replace("{id}", repository.Id),
            new GitStageRequest(["joint.txt"]), RelaxKonOSJsonOptions.Default);
        staged.EnsureSuccessStatusCode();
        var worktreeStatus = await businessClient.GetFromJsonAsync<GitStatusDto>(GitApiRoutes.Status.Replace("{id}", repository.Id),
            RelaxKonOSJsonOptions.Default);
        Require((await staged.Content.ReadFromJsonAsync<GitOperationResult>(RelaxKonOSJsonOptions.Default))?.Success == true
            && worktreeStatus?.Staged is [{ Path: "joint.txt" }], "the joint-installed Git package stages and reads the real Git index");
    }

    private static async Task VerifySignedTunnelsAsync(TestHost host, string root)
    {
        using var client = new HttpClient { BaseAddress = new Uri(host.Url) };
        using var anonymous = await client.GetAsync(TunnelApiRoutes.Profiles);
        Require(anonymous.StatusCode == System.Net.HttpStatusCode.Unauthorized, "the signed Tunnels routes require authentication");
        client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Admin);
        using var profileResponse = await client.PostAsJsonAsync(TunnelApiRoutes.Profiles,
            new UpsertTunnelServerProfileRequest("edge", "frps.example.test", 7000, TunnelAuthKind.None,
                TunnelTlsMode.Default, TunnelRuntimeMode.Managed, null), RelaxKonOSJsonOptions.Default);
        profileResponse.EnsureSuccessStatusCode();
        var profile = (await profileResponse.Content.ReadFromJsonAsync<TunnelServerProfileDto>(RelaxKonOSJsonOptions.Default))!;
        var request = new UpsertTunnelDefinitionRequest(profile.Id, "app",
            TunnelProtocol.Http, "127.0.0.1", 8080, null, null, true, false, false);
        using var rejected = await client.PostAsJsonAsync(TunnelApiRoutes.Tunnels, request, RelaxKonOSJsonOptions.Default);
        using var problem = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Require(rejected.StatusCode == System.Net.HttpStatusCode.BadRequest
            && problem.RootElement.GetProperty("problemCode").GetString() == "tunnel.domain_required",
            "the joint-installed signed Tunnels executor rejects a missing domain through authenticated HTTP");
        using var accepted = await client.PostAsJsonAsync(TunnelApiRoutes.Tunnels,
            request with { Domain = "app.example.test" }, RelaxKonOSJsonOptions.Default);
        accepted.EnsureSuccessStatusCode();
        var saved = (await accepted.Content.ReadFromJsonAsync<TunnelDefinitionDto>(RelaxKonOSJsonOptions.Default))!;
        var ownerProfiles = await client.GetFromJsonAsync<TunnelServerProfileDto[]>(TunnelApiRoutes.Profiles, RelaxKonOSJsonOptions.Default);
        Require(ownerProfiles is [var owned] && owned.Id == profile.Id, "the signed Tunnels route reports the saved profile to its owner");

        client.DefaultRequestHeaders.Remove("test-user");
        client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Observer);
        var observerProfiles = await client.GetFromJsonAsync<TunnelServerProfileDto[]>(TunnelApiRoutes.Profiles, RelaxKonOSJsonOptions.Default);
        Require(observerProfiles is [], "the observer can read profiles but cannot see a different account's profile");
        using var forbidden = await client.PostAsJsonAsync(TunnelApiRoutes.Tunnels,
            request with { Domain = "observer.example.test" }, RelaxKonOSJsonOptions.Default);
        Require(forbidden.StatusCode == System.Net.HttpStatusCode.Forbidden, "the observer cannot write a tunnel definition");

        client.DefaultRequestHeaders.Remove("test-user");
        client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.SecondAdmin);
        using var hidden = await client.GetAsync($"{TunnelApiRoutes.Tunnels}/{saved.Id}");
        Require(hidden.StatusCode == System.Net.HttpStatusCode.NotFound, "a different controller cannot read the owner's definition by ID");
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RelaxKonOSDbContext>();
        Require(await db.TunnelDefinitions.AnyAsync(row => row.Id == saved.Id && row.Domain == "app.example.test")
            && await db.TunnelDefinitions.CountAsync() == 1,
            "the signed Tunnels path saves only the valid definition to SQLite");

        if (Environment.GetEnvironmentVariable("RK_TEST_FRPC") is { Length: > 0 } executable)
        {
            var execution = host.Services.GetRequiredService<RelaxKonOS.ServerModule.Tunnels.ITunnelExecution>();
            var path = Path.Combine(root, "frpc.toml");
            await File.WriteAllTextAsync(path, execution.GenerateConfiguration(profile, [saved], null));
            var start = new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false };
            start.ArgumentList.Add("verify");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(path);
            using var frpc = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("frpc could not start.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await frpc.WaitForExitAsync(timeout.Token); }
            finally { if (!frpc.HasExited) frpc.Kill(); }
            Require(frpc.ExitCode == 0, "real frpc verifies the configuration generated by the joint-installed signed Tunnels executor");
            if (Environment.GetEnvironmentVariable("RK_TEST_FRPS_PORT") is { Length: > 0 } port)
            {
                client.DefaultRequestHeaders.Remove("test-user");
                client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Admin);
                await VerifyExternalFrpAsync(client, executable, int.Parse(port, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    private static async Task VerifyExternalFrpAsync(HttpClient client, string executable, int port)
    {
        var profileRequest = new UpsertTunnelServerProfileRequest("local-live", "127.0.0.1", port, TunnelAuthKind.None,
            TunnelTlsMode.Default, TunnelRuntimeMode.External, Path.GetFullPath(executable));
        using var response = await client.PostAsJsonAsync(TunnelApiRoutes.Profiles, profileRequest, RelaxKonOSJsonOptions.Default);
        response.EnsureSuccessStatusCode();
        var profile = (await response.Content.ReadFromJsonAsync<TunnelServerProfileDto>(RelaxKonOSJsonOptions.Default))!;
        using var backend = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        backend.Start();
        var localPort = ((System.Net.IPEndPoint)backend.LocalEndpoint).Port;
        using var reservation = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        reservation.Start();
        var remotePort = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var definition = await client.PostAsJsonAsync(TunnelApiRoutes.Tunnels,
            new UpsertTunnelDefinitionRequest(profile.Id, "loopback-probe", TunnelProtocol.Tcp, "127.0.0.1",
                localPort, remotePort, null, true, false, false), RelaxKonOSJsonOptions.Default);
        definition.EnsureSuccessStatusCode();
        var applyPath = TunnelApiRoutes.ApplyProfile.Replace("{profileId}", profile.Id.ToString("D"));
        var stopPath = TunnelApiRoutes.StopProfile.Replace("{profileId}", profile.Id.ToString("D"));
        var logPath = TunnelApiRoutes.ProfileLogs.Replace("{profileId}", profile.Id.ToString("D"));
        try
        {
            using var applied = await client.PostAsync(applyPath, null);
            applied.EnsureSuccessStatusCode();
            var result = await applied.Content.ReadFromJsonAsync<TunnelOperationResultDto>(RelaxKonOSJsonOptions.Default);
            Require(result?.Succeeded == true, $"the signed Tunnels route starts real external frpc: {result?.ProblemCode}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var logs = await client.GetFromJsonAsync<TunnelLogEntryDto[]>(logPath, RelaxKonOSJsonOptions.Default, timeout.Token);
                if (logs?.Any(log => log.Message.Contains("start proxy success", StringComparison.OrdinalIgnoreCase)) == true) break;
                await Task.Delay(100, timeout.Token);
            }
            using var forwarded = new System.Net.Sockets.TcpClient();
            await forwarded.ConnectAsync(System.Net.IPAddress.Loopback, remotePort, timeout.Token);
            var payload = "signed-tunnels-probe"u8.ToArray();
            await forwarded.GetStream().WriteAsync(payload, timeout.Token);
            using var received = await backend.AcceptTcpClientAsync(timeout.Token);
            var buffer = new byte[payload.Length];
            await received.GetStream().ReadExactlyAsync(buffer, timeout.Token);
            Require(buffer.AsSpan().SequenceEqual(payload), "real frpc and frps forward the exact payload to the loopback backend");
            await received.GetStream().WriteAsync(buffer, timeout.Token);
            Array.Clear(buffer);
            await forwarded.GetStream().ReadExactlyAsync(buffer, timeout.Token);
            Require(buffer.AsSpan().SequenceEqual(payload), "the loopback backend reply returns through the same FRP connection");
        }
        finally
        {
            using var stopped = await client.PostAsync(stopPath, null);
            stopped.EnsureSuccessStatusCode();
            var result = await stopped.Content.ReadFromJsonAsync<TunnelOperationResultDto>(RelaxKonOSJsonOptions.Default);
            Require(result is { Succeeded: true, State: TunnelConnectionState.Disconnected }, "Core stops the external FRP session");
        }
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var probe = new System.Net.Sockets.TcpClient();
            try { await probe.ConnectAsync(System.Net.IPAddress.Loopback, remotePort, stopTimeout.Token); }
            catch (System.Net.Sockets.SocketException error) when (error.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused)
            {
                Console.WriteLine("PASS: stopping the external session releases the frps proxy listener");
                break;
            }
            await Task.Delay(100, stopTimeout.Token);
        }
    }

    public static void RunLiveDatabases(string server, string archive, string state, string phase) =>
        RunWithDispatcher(() => RunLiveAsync(new Uri(server), Path.GetFullPath(archive), Path.GetFullPath(state), phase, ServerModules.DatabaseId));

    public static void RunLiveGit(string server, string archive, string state, string repository) =>
        RunWithDispatcher(() => RunLiveAsync(new Uri(server), Path.GetFullPath(archive), Path.GetFullPath(state), "reuse",
            ServerModules.GitId, repository));

    private static void RunWithDispatcher(Func<Task> run)
    {
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var task = run();
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        if (!task.IsCompleted) throw new TimeoutException("Joint deployment acceptance did not finish.");
        task.GetAwaiter().GetResult();
    }

    private static async Task RunLiveAsync(Uri server, string archive, string state, string phase, string moduleId,
        string? repository = null)
    {
        if (server.Scheme != "http" || server.Host != "127.0.0.1" || server.PathAndQuery != "/"
            || server.UserInfo.Length != 0 || server.Fragment.Length != 0 || phase is not ("start" or "resume" or "reuse" or "uninstall"))
            throw new ArgumentException("Expected a loopback Server and a start, resume, reuse or uninstall phase.");
        var username = Environment.GetEnvironmentVariable("RK_JOINT_FIXTURE_USER");
        var password = Environment.GetEnvironmentVariable("RK_JOINT_FIXTURE_PASSWORD");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Provide the dedicated fixture login.");
        using var loginHttp = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        var auth = new AuthSession(new RelaxKonOSClient(loginHttp), null!, null!);
        var platform = OperatingSystem.IsWindows() ? ClientPlatformKind.Windows : ClientPlatformKind.Linux;
        await auth.LoginAsync(ServerConnectionIdentityRules.Direct(server.AbsoluteUri),
            new(username, password, platform, "databases-joint-acceptance", "0.3.0"), false, false);
        using var moduleHttp = new HttpClient(new AuthenticatedHttpHandler(auth)
        {
            InnerHandler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false },
        });
        using var services = new ServiceCollection().BuildServiceProvider();
        using var trust = new ComponentCatalogSourceStore(Path.Combine(state, "trust"), "live-joint-fixture");
        var windows = new RelaxKonOS.WindowManager.WindowManager();
        var applications = new ApplicationManager(windows, services);
        var drive = new VirtualSystemDrive(Path.Combine(state, "drive"));
        var permissions = DispatchProxy.Create<IAppPermissionManager, PermissionProxy>();
        var factory = new LiveHttpFactory(moduleHttp);
        var packages = new DeveloperPackageManager(applications, null!, windows, permissions, new Diagnostics(), drive,
            Path.GetDirectoryName(archive)!, moduleHttp: factory, moduleSession: auth);
        await packages.LoadInstalledAsync();
        if (phase == "uninstall")
        {
            Require(await packages.UninstallAsync(ServerModules.DatabaseId) && !applications.IsRegistered(new AppId(ServerModules.DatabaseId)),
                "the desktop uninstalls its own Databases application");
            return;
        }
        var installer = new AppPackageInstallerService(packages, null!, trust);
        var deployment = new JointAppDeploymentService(factory, auth, installer, packages,
            applications, new FileDeploymentJournal(Path.Combine(state, "journal")));
        var candidate = await installer.InspectLocalAsync(archive);
        Require(candidate.Manifest.Id == moduleId, $"the fixture uses the {moduleId} desktop package");
        if (moduleId == ServerModules.GitId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(repository);
            var review = await deployment.PrepareAsync(candidate, [], default);
            Require(review.Plan.PreparedSteps.IsEmpty && review.Plan.OrderedReleases.Any(release =>
                    release.Package.Kind == DeploymentPackageKind.ServerModule && release.Package.Id == moduleId
                    && release.Satisfied && release.Capabilities.GetValueOrDefault(ServerCapabilities.Git) == RelaxKonOS.Protocol.Git.GitApiRoutes.CapabilityContractVersion),
                "Project Editor reuses the running Git module with its declared contract version");
            var installedGit = await deployment.ExecuteAsync(review, default);
            Require(installedGit.Id == moduleId && applications.IsRegistered(new AppId(moduleId)),
                "the desktop registers Project Editor after checking the live Git module");
            var git = new RelaxKonOS.Client.Apps.Git.RemoteGitClient(loginHttp, auth);
            var registered = await git.RegisterRepositoryAsync(new("Joint deployment fixture", repository));
            const string remoteName = "fixture-acceptance";
            Require((await git.ListRemotesAsync(registered.Id)).SingleOrDefault(remote => remote.Name == remoteName)
                    is { } remote && (remote.PushUrl ?? remote.FetchUrl) == repository + ".git",
                "the fixture push target is the dedicated local bare repository");
            var before = await git.GetGraphAsync(registered.Id);
            var beforeStatus = await git.GetStatusAsync(registered.Id);
            Require(before is [{ IsHead: true }] && beforeStatus.Staged.Select(item => item.Path).SequenceEqual(["staged.txt"])
                && beforeStatus.Untracked.Select(item => item.Path).SequenceEqual(["draft.txt"]),
                "the fixture starts with one Git commit and the isolated staged and untracked files");
            var commit = await git.CommitAsync(registered.Id, new("Project Editor fixture commit", ["draft.txt"]));
            Require(commit is { Success: true, Operation: "commit" }, "the desktop Git client commits the selected file");
            var after = await git.GetGraphAsync(registered.Id);
            var status = await git.GetStatusAsync(registered.Id);
            Require(after is [var head, var parent] && head.IsHead && parent.Sha == before[0].Sha
                && head.Parents.SequenceEqual([parent.Sha])
                && status.Staged.Select(item => item.Path).SequenceEqual(["staged.txt"])
                && status.Untracked.All(item => item.Path != "draft.txt"),
                "the commit advances HEAD and leaves unrelated staged work intact");
            var push = await git.PushAsync(registered.Id, new(SaveCredentials: false, LocalBranch: "main",
                Remote: remoteName, RemoteBranch: "fixture-verified"));
            Require(push is { Success: true, Operation: "push", RequiresCredentials: false },
                "the desktop Git client pushes the selected branch to the isolated bare repository");
            return;
        }
        if (phase == "start")
        {
            var review = await deployment.PrepareAsync(candidate, [], default);
            Require(review.Plan.PreparedSteps is [{ Package.Id: ServerModules.DatabaseId }],
                "the desktop prepares a signed Databases server package before installation");
            await ExpectRestartAsync(deployment, review);
            Require(!applications.IsRegistered(new AppId(ServerModules.DatabaseId)),
                "the desktop waits for the server module before registering Databases");
            return;
        }
        var resumed = phase == "reuse" ? await deployment.PrepareAsync(candidate, [], default)
            : await deployment.FindPendingAsync(candidate, default)
            ?? throw new InvalidOperationException("The desktop could not recover its original Databases deployment.");
        if (phase == "reuse")
            Require(resumed.Plan.PreparedSteps.IsEmpty
                && resumed.Plan.OrderedReleases.Any(release => release.Package.Kind == DeploymentPackageKind.ServerModule
                    && release.Package.Id == ServerModules.DatabaseId && release.Satisfied
                    && release.ArtifactIdentity.Split('/').Length == 3),
                "the desktop reuses the running signed Databases module");
        var installed = await deployment.ExecuteAsync(resumed, default);
        var manifest = applications.GetManifest(new AppId(installed.Id));
        Require(installed.Id == ServerModules.DatabaseId && manifest is not null,
            "the desktop plan registers Databases after verifying the running module");
        using var readinessServices = new ServiceCollection().AddSingleton(packages).AddSingleton(applications)
            .AddSingleton<RelaxKonOS.WindowManager.IWindowManager>(windows)
            .AddSingleton<IHttpClientFactory>(new LiveHttpFactory(moduleHttp)).BuildServiceProvider();
        using var readiness = new ApplicationAvailabilityService(auth, readinessServices, null!);
        Require(await readiness.RefreshAsync(manifest!) is { State: ComponentAvailabilityKind.NoPermission, PackageKind: null },
            "launch readiness accepts the signed module and reaches the separate component permission check");
    }

    private sealed class LiveHttpFactory(HttpClient http) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => http;
    }

    private static async Task RunAsync(string archive, IReadOnlyList<string> bundledModuleArchives)
    {
        var parent = Path.Combine(AppContext.BaseDirectory, "test-data");
        var root = Directory.CreateDirectory(Path.Combine(parent, "joint-deployment-" + Guid.NewGuid().ToString("N"))).FullName;
        var publish = Directory.CreateDirectory(Path.Combine(root, "module-publish")).FullName;
        var official = Directory.CreateDirectory(Path.Combine(root, "OfficialApps")).FullName;
        var serverRoot = Directory.CreateDirectory(Path.Combine(root, "server")).FullName;
        var packagePath = Path.Combine(official, AppIdText + ".roapp");
        TestHost? host = null;
        try
        {
            await PrepareDesktopArchiveAsync(archive, packagePath, optionalModuleId: ServerModules.FirewallId,
                capability: ServerCapabilities.Metrics, contractVersion: "2");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "FixtureModule.dll"), Path.Combine(publish, "FixtureModule.dll"));
            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var key = Path.Combine(root, "fixture-signing-key.pem");
            await File.WriteAllTextAsync(key, signer.ExportPkcs8PrivateKeyPem());
            var now = DateTimeOffset.UtcNow;
            var rid = ModulePackagePlatform.CurrentRid;
            var build = await ModulePackageBuilder.BuildAsync(new(publish, Path.Combine(root, "module-package"), ModuleId,
                "1.0.0", "joint-fixture-1.0.0-" + rid, "fixture", "official", rid, "FixtureModule.dll",
                "FixtureModule.FixtureInstalledModule", "joint-fixture", new string('a', 40), "official", 1,
                "https://modules.example/", key, "fixture-key", 3600,
                Ui: new("Joint Fixture", "", "", null, "UNLICENSED"), Capabilities: [ServerCapabilities.Metrics],
                CapabilityContractVersions: new Dictionary<string, string> { [ServerCapabilities.Metrics] = "2" }), now);
            try
            {
                ModulePackageVerifier.ValidateManifest(build.Manifest with
                {
                    CapabilityContractVersions = new Dictionary<string, string> { [ServerCapabilities.Files] = "2" }
                });
                throw new InvalidOperationException("Expected an undeclared capability contract to be rejected.");
            }
            catch (ModulePackageException error) when (error.ProblemCode == "module-package-capability-contract-version-invalid") { }
            File.Delete(key);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ModuleManagement:Authority"] = "managed",
                ["ModulePackages:Sources:official:SchemaVersion"] = "1",
                ["ModulePackages:Sources:official:SourceId"] = "official",
                ["ModulePackages:Sources:official:Revision"] = "1",
                ["ModulePackages:Sources:official:Enabled"] = "true",
                ["ModulePackages:Sources:official:PublisherId"] = "fixture",
                ["ModulePackages:Sources:official:Purpose"] = "official",
                ["ModulePackages:Sources:official:ArtifactOrigin"] = "https://modules.example/",
                ["ModulePackages:Sources:official:AuthorizedModuleIds:0"] = ModuleId,
                ["ModulePackages:Sources:official:PublicKeysSpki:fixture-key"] = build.PublicKeySpkiBase64,
                ["ModulePackages:Sources:official:MaximumValiditySeconds"] = "3600",
            }).Build();

            string serviceId;
            using (var store = new ModuleManagementStore(config, serverRoot)) serviceId = store.ServiceId;
            using (var catalog = new ModulePackageCatalogStore(serverRoot, serviceId))
                catalog.Accept(await File.ReadAllBytesAsync(build.CatalogPath), ModulePackageHostConfiguration.Source(config, "official"),
                    new ModulePackageContext(serviceId, rid, ModulePackageHostConfiguration.HostApiVersion, false),
                    ModulePackageCatalogStore.InitialRevision,
                    ModuleTestAuthentication.Admin, "joint-accept", now);

            host = await TestHost.StartAsync(serverRoot, config);
            var auth = DispatchProxy.Create<IAuthSession, SessionProxy>();
            var session = (SessionProxy)(object)auth;
            session.Url = host.Url;
            using var factory = new FixtureHttpFactory(await File.ReadAllBytesAsync(build.ArchivePath));
            using var services = new ServiceCollection().BuildServiceProvider();
            var windows = new RelaxKonOS.WindowManager.WindowManager();
            var applications = new ApplicationManager(windows, services);
            var drive = new VirtualSystemDrive(Path.Combine(root, "desktop-drive"));
            var permissions = DispatchProxy.Create<IAppPermissionManager, PermissionProxy>();
            var packages = new DeveloperPackageManager(applications, null!, windows, permissions, new Diagnostics(), drive, official,
                moduleHttp: factory, moduleSession: auth);
            await packages.LoadInstalledAsync();
            using var trust = new ComponentCatalogSourceStore(Path.Combine(root, "component-trust"), "joint-fixture");
            var installer = new AppPackageInstallerService(packages, null!, trust);
            var journalPath = Path.Combine(root, "journal");
            JointAppDeploymentService Service() => new(factory, auth, installer, packages, applications, new(journalPath));
            var candidate = await installer.InspectLocalAsync(packagePath);
            var joint = Service();
            const string prerequisiteId = "fixture.prerequisite";
            var prerequisitePath = Path.Combine(root, "prerequisite.roapp");
            await RewriteDesktopArchiveAsync(archive, prerequisitePath, manifest => manifest["id"] = prerequisiteId);
            await installer.InstallAsync(await installer.InspectLocalAsync(prerequisitePath));
            var mixedPath = Path.Combine(root, "mixed.roapp");
            await RewriteDesktopArchiveAsync(packagePath, mixedPath, manifest =>
            {
                manifest["id"] = "fixture.mixed";
                manifest["dependencies"]!["required"]!.AsArray().Add(new JsonObject
                {
                    ["package"] = new JsonObject { ["kind"] = (int)DeploymentPackageKind.Application, ["id"] = prerequisiteId },
                });
            });
            var mixedCandidate = await installer.InspectLocalAsync(mixedPath);
            var mixed = await joint.PrepareAsync(mixedCandidate, [], default);
            Require(mixed.Plan.OrderedReleases.Select(release => release.Package.Id)
                    .SequenceEqual([ModuleId, prerequisiteId, mixedCandidate.Manifest.Id]),
                "the mixed plan orders the module before the local prerequisite");
            factory.BeforeNextRequest = () => packages.UninstallAsync(prerequisiteId);
            try
            {
                await joint.ExecuteAsync(mixed, default);
                throw new InvalidOperationException("the mixed plan accepted a removed prerequisite");
            }
            catch (InvalidOperationException error) when (error.Message == "deployment-client-dependency-changed") { }
            Require(!applications.IsRegistered(new AppId(prerequisiteId))
                && ModulePackageOperationStore.Read(serverRoot).Count == 0,
                "removing a local prerequisite during snapshot observation prevents the module operation");
            var reopenedMixedJournal = new FileDeploymentJournal(journalPath);
            Require(await Service().FindPendingAsync(mixedCandidate, default) is null
                && (await reopenedMixedJournal.ReadPendingRecoveriesAsync(auth.CurrentUser!.Id, auth.ServiceId!))
                    .Any(recovery => recovery.Plan.Id == mixed.Plan.Id && recovery.RequiresReview),
                "the reopened journal offers a new review instead of resuming the invalidated mixed plan");

            var optional = candidate.Manifest.Dependencies!.Optional.Single();
            var requestsBeforeInvalidSelection = factory.Requests;
            try
            {
                await joint.PrepareAsync(candidate, [optional with { MinimumVersion = "9.0.0" }], default);
                throw new InvalidOperationException("an altered optional dependency was accepted");
            }
            catch (InvalidOperationException error) when (error.Message == "deployment-optional-selection-invalid") { }
            Require(factory.Requests == requestsBeforeInvalidSelection, "altered optional requirements are rejected before server requests");
            var withOptional = await joint.PrepareAsync(candidate, [optional], default);
            Require(withOptional.Plan.OrderedReleases.Any(release => release.Package == optional.Package && release.Satisfied),
                "selecting the declared optional module keeps it in the plan");
            var review = await joint.PrepareAsync(candidate, [], default);
            Require(review.Plan.PreparedSteps.Length == 1 && review.Plan.OrderedReleases[0].Package.Id == ModuleId
                && review.Plan.OrderedReleases.All(release => release.Package != optional.Package),
                "deselecting the optional module preserves the missing required signed module");
            var settings = new ShellSettings(null!);
            var localization = new LocalizationService(settings, new SshDesktopSession(null!, settings));
            using (var uiServices = new ServiceCollection().AddSingleton(localization).BuildServiceProvider())
            {
                typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, uiServices);
                using var model = new AppInstallerViewModel(installer) { JointDeployment = joint, CurrentPackage = candidate };
                model.SelectOptionalAsync = _ => Task.FromResult<IReadOnlyList<DeploymentRequirement>?>([]);
                var requestsBeforeLoginChange = 0;
                var confirmations = 0;
                model.ConfirmDeploymentAsync = _ =>
                {
                    if (++confirmations != 1) return Task.FromResult(false);
                    requestsBeforeLoginChange = factory.Requests;
                    factory.BeforeNextClient = () => session.Url = host.Url;
                    return Task.FromResult(true);
                };
                await model.InstallCommand.ExecuteAsync(null);
                Require(factory.Requests == requestsBeforeLoginChange
                    && !applications.IsRegistered(new AppId(AppIdText)),
                    "a replacement login during client binding sends no module request");
                await model.InstallCommand.ExecuteAsync(null);
                Require(confirmations == 2 && !applications.IsRegistered(new AppId(AppIdText)),
                    "retry after a replacement login requires a fresh confirmation");
            }
            factory.SubstituteServerIdentity = Guid.NewGuid().ToString("D");
            try
            {
                await joint.ExecuteAsync(review, default);
                throw new InvalidOperationException("Expected a changed server identity to reject the original plan.");
            }
            catch (InvalidOperationException error) when (error.Message == "deployment-server-changed") { }
            finally { factory.SubstituteServerIdentity = null; }
            Require(!applications.IsRegistered(new AppId(AppIdText))
                && ModulePackageOperationStore.Read(serverRoot).Count == 0
                && await new FileDeploymentJournal(journalPath).FindStartedPlanAsync(review.Plan.Id) is null,
                "a different server identity at the same address rejects the old plan before creating a checkpoint or applying it");
            await ExpectRestartAsync(joint, review);
            Require(!applications.IsRegistered(new AppId(AppIdText)) && InstalledModuleStore.Read(serverRoot).Single().ModuleId == ModuleId,
                "the first execution installs the signed module and leaves the desktop package pending");

            factory.SubstituteReceiptRelease = true;
            try
            {
                await joint.ExecuteAsync(review, default);
                throw new InvalidOperationException("Expected the substituted receipt to be rejected.");
            }
            catch (InvalidOperationException error) when (error.Message == "deployment-module-receipt-unavailable") { }
            finally { factory.SubstituteReceiptRelease = false; }
            Require(!applications.IsRegistered(new AppId(AppIdText)) && factory.SubmittedEnableApplies == 0,
                "a receipt with a mismatched release cannot enable the module or install the application");

            await host.DisposeAsync();
            host = null;
            host = await TestHost.StartAsync(serverRoot, config);
            session.Url = host.Url;
            joint = Service();
            session.Online = false;
            session.KeepDisconnectedIdentity = true;
            Require(await joint.FindPendingAsync(candidate, default) is null,
                "a disconnected client cannot recover a server plan using cached account and service IDs");
            session.KeepDisconnectedIdentity = false;
            session.Online = true;
            var originalAccount = session.AccountId;
            var requestsBeforeAccountChange = factory.Requests;
            session.AccountId = Guid.NewGuid();
            try
            {
                Require(await joint.FindPendingAsync(candidate, default) is null,
                    "a different account on the same connection cannot find the original pending plan");
                try
                {
                    await joint.ExecuteAsync(review, default);
                    throw new InvalidOperationException("Expected the other account to be unable to execute the original review.");
                }
                catch (InvalidOperationException error) when (error.Message == "deployment-account-changed") { }
                Require(factory.Requests == requestsBeforeAccountChange,
                    "the rejected account does not send any module requests");
            }
            finally { session.AccountId = originalAccount; }
            var originalConnection = session.ConnectionIdentity;
            session.ConnectionIdentity = "different-joint-test-server";
            try
            {
                Require(await joint.FindPendingAsync(candidate, default) is null,
                    "the same account on a different connection cannot find the pending plan");
                Require((await joint.RecoverArchivesAsync([], default)).Count == 0,
                    "the recovery queue does not surface archives from a different connection");
                Require(factory.Requests == requestsBeforeAccountChange,
                    "looking for recovery on the other connection does not send module requests");
            }
            finally { session.ConnectionIdentity = originalConnection; }
            review = await joint.FindPendingAsync(candidate, default) ?? throw new InvalidOperationException("Missing pending deployment.");
            using (var cancellation = new CancellationTokenSource())
            {
                var entered = factory.HoldNextEnableApply();
                var execution = joint.ExecuteAsync(review, cancellation.Token);
                await entered.WaitAsync(TimeSpan.FromSeconds(10));
                cancellation.Cancel();
                try
                {
                    await execution.WaitAsync(TimeSpan.FromSeconds(10));
                    throw new InvalidOperationException("Expected the blocked enable request to be cancelled.");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            Require(factory.SubmittedEnableApplies == 0 && !applications.IsRegistered(new AppId(AppIdText)),
                "cancelling an in-flight enable request leaves the application pending without submitting enable");
            factory.DropNextEnableResponse();
            await ExpectInterruptedEnableAsync(joint, review);
            Require(factory.EnablePreviewKeys.Distinct(StringComparer.Ordinal).Count() == 2,
                "retry after an interrupted enable requests a fresh server preview");
            await ExpectRestartAsync(joint, review);
            Require(factory.EnablePreviewKeys.Count == 2 && factory.SubmittedEnableApplies == 1,
                "a lost enable response is reconciled from the pending server state without submitting enable again");
            Require(!applications.IsRegistered(new AppId(AppIdText)), "the desktop stays pending while module enable awaits restart");

            await host.DisposeAsync();
            host = null;
            host = await TestHost.StartAsync(serverRoot, config);
            session.Url = host.Url;
            joint = Service();
            review = await joint.FindPendingAsync(candidate, default) ?? throw new InvalidOperationException("Missing deployment after enable.");
            var failureJournal = new FileDeploymentJournal(Path.Combine(journalPath, "failed-package"));
            var failureService = new JointAppDeploymentService(factory, auth, installer, packages, applications, failureJournal);
            await using (var lease = await failureJournal.OpenAsync(review.Plan, default))
                await lease.SaveAsync(new DeploymentCheckpoint(0, false), default);
            factory.SubstituteFailedPackage = true;
            try
            {
                await failureService.ExecuteAsync(review, default);
                throw new InvalidOperationException("Expected a failed package receipt to require a new review.");
            }
            catch (ModulePackageDeploymentFailedException error) when (error.ModuleId == ModuleId
                && error.OperationId != Guid.Empty && error.Message == "fixture-package-failure") { }
            finally { factory.SubstituteFailedPackage = false; }
            var reopenedFailure = new JointAppDeploymentService(factory, auth, installer, packages, applications,
                new FileDeploymentJournal(Path.Combine(journalPath, "failed-package")));
            var recoveredFailure = await reopenedFailure.RecoverArchivesAsync([], default);
            Require(!applications.IsRegistered(new AppId(AppIdText))
                && await reopenedFailure.FindPendingAsync(candidate, default) is null
                && recoveredFailure is [{ IsRecoveredDeployment: true, RequiresDeploymentReview: true } recovered]
                && recovered.ExpectedSha256 == review.Plan.OrderedReleases[^1].ArtifactIdentity,
                "a terminal package failure recovers the exact desktop archive for review instead of resuming execution");
            factory.DropDependencyReply = true;
            factory.BlockDependencyRelease = true;
            try
            {
                await joint.ExecuteAsync(review, default);
                throw new InvalidOperationException("Expected the dependency registration reply to be lost.");
            }
            catch (HttpRequestException error) when (error.Message == "fixture-dependency-reply-lost") { }
            var abandonedRelease = drive.ResolveRootChild($"System/desktop-module-releases/{review.Plan.Id:N}.json");
            Require(!applications.IsRegistered(new AppId(AppIdText)) && File.Exists(abandonedRelease),
                "a lost dependency reply leaves an uncommitted application and a durable release record");
            factory.BlockDependencyRelease = false;
            await packages.LoadInstalledAsync();
            Require(!File.Exists(abandonedRelease), "startup reconciles the uncommitted dependency before retry");
            factory.BlockHealthWhen = () => applications.IsRegistered(new AppId(AppIdText));
            try
            {
                await joint.ExecuteAsync(review, default);
                throw new InvalidOperationException("Expected final module health to prevent deployment completion.");
            }
            catch (InvalidOperationException error) when (error.Message == "deployment-module-unhealthy") { }
            finally { factory.BlockHealthWhen = null; }
            var committedPath = packages.FindInstalled(AppIdText)?.InstallationPath;
            await using (var lease = await new FileDeploymentJournal(journalPath).OpenAsync(review.Plan, default))
                Require(committedPath is not null && lease.Checkpoint is { Verified: false, RequiresReview: false } pending
                    && pending.CompletedSteps == review.Plan.OrderedReleases.Length,
                    "failed final health leaves the committed application awaiting verification");
            joint = Service();
            var resumedCandidate = (await joint.RecoverArchivesAsync([], default))
                .Single(item => item.ExpectedSha256 == review.Plan.OrderedReleases[^1].ArtifactIdentity);
            using (var uiServices = new ServiceCollection().AddSingleton(localization).BuildServiceProvider())
            {
                typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, uiServices);
                using var model = new AppInstallerViewModel(installer)
                {
                    JointDeployment = joint,
                    CurrentPackage = resumedCandidate,
                    ConfirmDeploymentAsync = plan => Task.FromResult(plan.Id == review.Plan.Id),
                };
                Require(resumedCandidate.IsRepair && model.ActionLabel == localization.Get("app_installer.deployment.resume", ""),
                    "the installer offers to resume an already committed deployment");
                await model.InstallCommand.ExecuteAsync(null);
                var current = packages.FindInstalled(AppIdText);
                Require(current is not null && current.InstallationPath == committedPath && model.CurrentPackage is null
                    && model.Status.Resolve() == string.Format(localization.Get("app_installer.status.installed", ""),
                        current.DisplayName, current.Version),
                    "resuming the final health check preserves the installation and reports the original install");
            }
            var installed = packages.FindInstalled(AppIdText)!;
            var executor = new ModuleDeploymentExecutor(new(factory.CreateClient("joint-fixture"), auth));
            var moduleRelease = review.Plan.OrderedReleases.Single(item => item.Package.Id == ModuleId);
            factory.FaultedModule = ModuleId;
            try
            {
                await executor.VerifyAsync(review.Plan, moduleRelease, default);
                throw new InvalidOperationException("Expected module recovery to block joint deployment verification.");
            }
            catch (ModuleDeploymentRecoveryException error) when (error.ModuleId == ModuleId) { }
            finally { factory.FaultedModule = null; }
            factory.SubstituteHealthVersion = true;
            try
            {
                await executor.VerifyAsync(review.Plan, moduleRelease, default);
                throw new InvalidOperationException("Expected mismatched running version to be rejected.");
            }
            catch (InvalidOperationException error) when (error.Message == "deployment-module-unhealthy") { }
            finally { factory.SubstituteHealthVersion = false; }
            await executor.VerifyAsync(review.Plan, moduleRelease, default);
            factory.SubstituteInstalledDigest = true;
            try
            {
                await executor.VerifyAsync(review.Plan, moduleRelease, default);
                throw new InvalidOperationException("Expected changed installed package identity to be rejected.");
            }
            catch (InvalidOperationException error) when (error.Message == "deployment-module-not-effective") { }
            finally { factory.SubstituteInstalledDigest = false; }
            factory.SubstituteBlockedHealth = true;
            try
            {
                var report = await new ServerModuleManagementClient(factory.CreateClient("joint-fixture"), auth).CheckHealthAsync();
                Require(report.Modules.Single(item => item.ModuleId == ModuleId) is
                    { Status: "blocked", ProblemCode: "module-host-code-trust-required" },
                    "the desktop accepts a valid health report containing a blocked ModuleHost");
            }
            finally { factory.SubstituteBlockedHealth = false; }
            try
            {
                await joint.ExecuteAsync(review with
                {
                    Candidate = review.Candidate with { ExpectedSha256 = new string('0', 64) },
                }, default);
                throw new InvalidOperationException("Expected the completed plan to reject a different candidate.");
            }
            catch (InvalidOperationException error) when (error.Message == "deployment-application-plan-changed") { }
            Require(await joint.ExecuteAsync(review, default) == installed,
                "completed deployment replay accepts the confirmed application archive identity");
            using var ready = new HttpClient { BaseAddress = new Uri(host.Url) };
            ready.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Admin);
            var health = await ready.GetFromJsonAsync<ServerModuleHealthPageDto>(
                ServerModuleManagementRoutes.ModuleHealth, RelaxKonOSJsonOptions.Default);
            Require(installed.Id == AppIdText && applications.IsRegistered(new AppId(AppIdText))
                && health?.Modules.Single(item => item.ModuleId == ModuleId) is { Enabled: true, Version: "1.0.0", Status: "healthy" }
                && ModulePackageOperationStore.Read(serverRoot).Count == 1
                && await ready.GetStringAsync("/api/v1.0/fixture-module/ping") == "pong"
                && await ready.GetStringAsync("/fixture-core") == "ready"
                && await joint.FindPendingAsync(candidate, default) is null,
                "the resumed deployment verifies the running module, Core and desktop application registration");
            using (var readinessServices = new ServiceCollection().AddSingleton(packages).AddSingleton(applications)
                .AddSingleton<IHttpClientFactory>(factory).BuildServiceProvider())
            using (var readiness = new ApplicationAvailabilityService(auth, readinessServices, null!))
            {
                var manifest = applications.GetManifest(new AppId(AppIdText))!;
                var requestsBeforeReadiness = factory.Requests;
                factory.BeforeNextClient = () => session.Url = host.Url;
                await readiness.RefreshAsync(manifest);
                Require(factory.Requests == requestsBeforeReadiness,
                    "launch readiness does not query modules using a replacement login");
                Require(await readiness.RefreshAsync(manifest) is { CanOpen: true }, "a healthy module satisfies launch readiness");
                factory.HideEffectiveVersion = true;
                Require(await readiness.RefreshAsync(manifest) is
                    { CanOpen: false, State: ComponentAvailabilityKind.Unknown, ComponentId: ModuleId },
                    "an installed independent module without a running version blocks launch without reporting it missing");
                factory.HideEffectiveVersion = false;
                var moduleCatalog = new ModuleDeploymentCatalog(new ServerModuleManagementClient(factory.CreateClient("joint-fixture"), auth));
                var healthy = await moduleCatalog.ReadAsync(default);
                Require(healthy.Releases.Single(release => release.Package.Id == ModuleId && release.Satisfied)
                    .Capabilities.GetValueOrDefault(ServerCapabilities.Metrics) == "2",
                    "the signed package capability contract is preserved by installed module inventory");
                factory.SubstituteCapabilities = true;
                Require(await moduleCatalog.ObserveAsync(default) != healthy.Target,
                    "changing only the module's advertised capabilities invalidates the deployment target");
                var advertised = await moduleCatalog.ReadAsync(default);
                var versioned = advertised.Releases.Single(release => release.Package.Id == ModuleId && release.Satisfied);
                Require(versioned.Capabilities.GetValueOrDefault("fixture.changed") == "2",
                    "the effective candidate preserves the explicit capability contract version");
                factory.ObservedCapabilityVersion = "3";
                Require(await moduleCatalog.ObserveAsync(default) != advertised.Target,
                    "changing only a capability contract version invalidates the deployment target");
                var versionExecutor = new ModuleDeploymentExecutor(new(factory.CreateClient("joint-fixture"), auth));
                try
                {
                    await versionExecutor.VerifyAsync(review.Plan, versioned, default);
                    throw new InvalidOperationException("Expected exact capability contract verification to fail.");
                }
                catch (InvalidOperationException error) when (error.Message == "deployment-module-not-effective") { }
                factory.ObservedCapabilityVersion = "2";
                await versionExecutor.VerifyAsync(review.Plan, versioned, default);
                factory.SubstituteCapabilities = false;
                factory.SubstituteInstalledDigest = true;
                Require(await moduleCatalog.ObserveAsync(default) != healthy.Target,
                    "changing only the active installed archive invalidates the deployment target");
                factory.SubstituteInstalledDigest = false;
                factory.SubstituteInventoryDigest = true;
                var mismatched = await moduleCatalog.ReadAsync(default);
                Require(!mismatched.Releases.Any(release => release.Package.Id == ModuleId && release.Satisfied),
                    "a running independent module with a mismatched installed digest is not reusable");
                factory.SubstituteInventoryDigest = false;
                factory.FaultedModule = ModuleId;
                Require(await readiness.RefreshAsync(manifest) is
                    { CanOpen: false, State: ComponentAvailabilityKind.Recovery, ComponentId: ModuleId },
                    "a faulted module cannot satisfy launch readiness even when effectively enabled");
                var faulted = await moduleCatalog.ReadAsync(default);
                Require(healthy.Releases.Any(release => release.Package.Id == ModuleId && release.Satisfied)
                    && faulted.Releases.All(release => release.Package.Id != ModuleId || !release.Satisfied)
                    && healthy.Target.StateRevision != faulted.Target.StateRevision,
                    "a faulted module invalidates the deployment snapshot and is never a satisfied candidate");
                factory.FaultedModule = null;
            }
            var protectionClient = new ServerModuleManagementClient(factory.CreateClient("joint-fixture"), auth);
            var protectedInventory = await protectionClient.ReadAsync();
            var disableProtected = await protectionClient.PreviewAsync(new(serviceId, ServerModuleAction.Disable, [ModuleId],
                protectedInventory.Where(module => module.DesiredEnabled && module.Id != ModuleId).Select(module => module.Id).ToArray(),
                protectedInventory[0].DesiredRevision!, "verify-registered-dependency"));
            Require(disableProtected.BlockedBy.Any(blocker => blocker.ProblemCode == "module-desktop-dependency-blocked" && blocker.ModuleId == ModuleId),
                "committing the application registers a server-side blocker for its required module");
            var upgradeSource = Path.Combine(root, "versioned-upgrade-source.roapp");
            File.Copy(packagePath, upgradeSource);
            await RewriteDesktopArchiveAsync(upgradeSource, packagePath, manifest => manifest["version"] = "1.1.0");
            await using (var upgradedArchive = File.OpenRead(packagePath))
                await File.WriteAllTextAsync(packagePath + ".sha256", Convert.ToHexStringLower(await SHA256.HashDataAsync(upgradedArchive)));
            var upgrade = await joint.PrepareAsync(await installer.InspectLocalAsync(packagePath), [], default);
            Require((await joint.ExecuteAsync(upgrade, default)).Version == "1.1.0",
                "upgrading the desktop application commits a replacement module dependency");
            factory.DropDependencyReply = true;
            factory.BlockDependencyRelease = true;
            var interruptedRollback = await packages.SwitchAsync(AppIdText, rollback: true, launch: false, default);
            Require(!interruptedRollback.Ok && packages.FindInstalled(AppIdText)?.Version == "1.1.0"
                && File.Exists(abandonedRelease),
                $"an interrupted rollback leaves the current version and queues its uncommitted dependency ({interruptedRollback.Code}, {packages.FindInstalled(AppIdText)?.Version}, queued={File.Exists(abandonedRelease)})");
            factory.BlockDependencyRelease = false;
            await packages.LoadInstalledAsync();
            Require(!File.Exists(abandonedRelease), "startup reconciles the uncommitted rollback dependency");
            Require((await packages.SwitchAsync(AppIdText, rollback: true, launch: false, default)).Ok
                && packages.FindInstalled(AppIdText)?.Version == installed.Version,
                "rolling back the desktop application restores its original module dependency before switching");

            var secondWindows = new RelaxKonOS.WindowManager.WindowManager();
            var secondApplications = new ApplicationManager(secondWindows, services);
            var secondDrive = new VirtualSystemDrive(Path.Combine(root, "second-desktop-drive"));
            var secondPackages = new DeveloperPackageManager(secondApplications, null!, secondWindows, permissions,
                new Diagnostics(), secondDrive, official, moduleHttp: factory, moduleSession: auth);
            await secondPackages.LoadInstalledAsync();
            var secondInstaller = new AppPackageInstallerService(secondPackages, null!, trust);
            var secondCoordinator = new JointAppDeploymentService(factory, auth, secondInstaller, secondPackages,
                secondApplications, new(Path.Combine(root, "second-desktop-journal")));
            var secondReview = await secondCoordinator.PrepareAsync(await secondInstaller.InspectLocalAsync(packagePath), [], default);
            Require(secondReview.Plan.Id != review.Plan.Id
                && (await secondCoordinator.ExecuteAsync(secondReview, default)).Version == "1.1.0",
                "a second desktop under the same account independently registers the same module dependency");

            session.Online = false;
            await packages.UninstallAsync(AppIdText);
            var releaseDirectory = drive.ResolveRootChild("System/desktop-module-releases");
            Require(Directory.EnumerateFiles(releaseDirectory, "*.json").Any(),
                "offline desktop uninstall queues its server dependency release");
            session.Online = true;
            var sharedClient = new ServerModuleManagementClient(factory.CreateClient("joint-fixture"), auth);
            var sharedInventory = await sharedClient.ReadAsync();
            var sharedDisable = new ServerModulePlanRequest(serviceId, ServerModuleAction.Disable, [ModuleId],
                sharedInventory.Where(module => module.DesiredEnabled && module.Id != ModuleId).Select(module => module.Id).ToArray(),
                sharedInventory[0].DesiredRevision!, "second-desktop-still-installed");
            Require((await sharedClient.PreviewAsync(sharedDisable with { IdempotencyKey = "offline-release-queued" })).BlockedBy.Any(blocker =>
                    blocker.ProblemCode == "module-desktop-dependency-blocked" && blocker.ModuleId == ModuleId),
                "the server still blocks disable while the offline release is queued");
            Require(await secondPackages.UninstallAsync(AppIdText),
                "the second desktop can uninstall and release its claim while the first release is queued");
            var secondReleaseDirectory = secondDrive.ResolveRootChild("System/desktop-module-releases");
            Require(!Directory.Exists(secondReleaseDirectory) || !Directory.EnumerateFiles(secondReleaseDirectory, "*.json").Any(),
                "the online uninstall leaves no deferred release on the second desktop");
            Require((await sharedClient.PreviewAsync(sharedDisable with { IdempotencyKey = "only-offline-release-queued" })).BlockedBy.Any(blocker =>
                    blocker.ProblemCode == "module-desktop-dependency-blocked" && blocker.ModuleId == ModuleId),
                "the unreported offline uninstall alone keeps module protection in force");
            await packages.LoadInstalledAsync();
            Require(!Directory.EnumerateFiles(releaseDirectory, "*.json").Any(),
                "startup releases an offline uninstall record on the original server and account");
            Require((await sharedClient.PreviewAsync(sharedDisable with { IdempotencyKey = "both-claims-released" })).BlockedBy.Count == 0,
                "reconciling the last claim allows the module to be disabled");

            await installer.InstallAsync(await installer.InspectLocalAsync(prerequisitePath));
            var replacementMixed = await joint.PrepareAsync(mixedCandidate, [], default);
            Require(replacementMixed.Plan.Id != mixed.Plan.Id
                && replacementMixed.Plan.OrderedReleases.Take(2).All(release => release.Satisfied),
                "reviewing again after restoring the local prerequisite reuses the now healthy module");
            var mixedInstalled = await joint.ExecuteAsync(replacementMixed, default);
            Require(mixedInstalled.Id == mixedCandidate.Manifest.Id
                && applications.IsRegistered(new AppId(mixedInstalled.Id))
                && ModulePackageOperationStore.Read(serverRoot).Count == 1
                && !(await reopenedMixedJournal.ReadPendingRecoveriesAsync(auth.CurrentUser!.Id, auth.ServiceId!))
                    .Any(recovery => recovery.Plan.OrderedReleases[^1].Package.Id == mixedInstalled.Id),
                "completing the replacement plan retires the old recovery entry without installing the module again");
            await packages.UninstallAsync(mixedInstalled.Id);
            await packages.UninstallAsync(prerequisiteId);

            var activationArchives = bundledModuleArchives.Where(path =>
                Path.GetFileNameWithoutExtension(path) is ServerModules.FirewallId or ServerModules.GitId
                    or ServerModules.DatabaseId or ServerModules.DockerId or "relaxkonos.webservers" or "relaxkonos.tunnels" or "relaxkonos.tailscale")
                .ToDictionary(path => Path.GetFileNameWithoutExtension(path) == "relaxkonos.webservers"
                    ? ServerModules.WebServerId : Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal);
            var activationDependencies = new[] { ServerModules.FirewallId, ModuleId }
                .Concat(new[] { ServerModules.GitId, ServerModules.DatabaseId, ServerModules.DockerId, ServerModules.WebServerId, "relaxkonos.tunnels", "relaxkonos.tailscale" }
                    .Where(activationArchives.ContainsKey));
            foreach (var dependencyId in activationDependencies)
            {
                var moduleClient = new ServerModuleManagementClient(factory.CreateClient("joint-fixture"), auth);
                var inventory = await moduleClient.ReadAsync();
                var enabled = inventory.Where(module => module.DesiredEnabled && module.Id != dependencyId)
                    .Select(module => module.Id).Order(StringComparer.Ordinal).ToArray();
                var disable = await moduleClient.PreviewAsync(new(serviceId, ServerModuleAction.Disable,
                    [dependencyId], enabled, inventory[0].DesiredRevision!, "disable-for-joint-test-" + dependencyId));
                Require(disable.BlockedBy.Count == 0,
                    $"the isolated host previews disabling {dependencyId}: "
                    + string.Join(", ", disable.BlockedBy.Select(item => $"{item.ProblemCode}/{item.ModuleId}")));
                var disabled = await moduleClient.ApplyAsync(disable.PlanId);
                Require(disabled.State == ServerModuleOperationState.AwaitingRestart,
                    $"the isolated host accepts disabling {dependencyId} ({disabled.State}/{disabled.ProblemCode})");
                await host.DisposeAsync();
                host = await TestHost.StartAsync(serverRoot, config);
                session.Url = host.Url;
                joint = Service();
                var restartedClient = new ServerModuleManagementClient(factory.CreateClient("joint-fixture"), auth);
                var stoppedInventory = await restartedClient.ReadAsync();
                var stopped = stoppedInventory.Single(module => module.Id == dependencyId);
                var stopReceipt = await restartedClient.OperationAsync(disabled.OperationId);
                Require(!stopped.DesiredEnabled && !stopped.EffectiveEnabled && !stopped.RestartRequired
                    && stopReceipt.State == ServerModuleOperationState.Succeeded,
                    $"restart completes the disable receipt and makes {dependencyId} ineffective");

                var officialArchive = activationArchives.GetValueOrDefault(dependencyId);
                var activationAppId = officialArchive is null ? "fixture.bundled" : Path.GetFileNameWithoutExtension(officialArchive);
                var activationArchive = officialArchive is null
                    ? Path.Combine(root, dependencyId + "-dependent.roapp")
                    : Path.Combine(official, activationAppId + ".roapp");
                if (officialArchive is not null)
                {
                    File.Copy(officialArchive, activationArchive);
                    File.Copy(officialArchive + ".sha256", activationArchive + ".sha256");
                }
                else
                    await PrepareDesktopArchiveAsync(archive, activationArchive, activationAppId,
                        new DeploymentPackageKey(DeploymentPackageKind.ServerModule, dependencyId));
                var activationCandidate = await installer.InspectLocalAsync(activationArchive);
                var activationReview = await joint.PrepareAsync(activationCandidate, [], default);
                Require(activationReview.Plan.PreparedSteps.IsEmpty &&
                    activationReview.Plan.OrderedReleases.Single(release => release.ActivationOnly)
                        is { Satisfied: false } activation && activation.Package.Id == dependencyId,
                    $"{dependencyId} is reviewed as enable-only without a package download");
                if (dependencyId == ServerModules.WebServerId)
                    Require(activationReview.Plan.OrderedReleases.Select(release => release.Package.Id)
                            .SequenceEqual([ServerModules.CertificatesId, ServerModules.WebServerId, activationAppId])
                        && activationReview.Plan.OrderedReleases[0].Satisfied,
                        "the Web Server Manager review reuses Certificates before activating Web Servers");
                await ExpectRestartAsync(joint, activationReview);
                Require(!applications.IsRegistered(new AppId(activationAppId))
                    && ModulePackageOperationStore.Read(serverRoot).Count == 1,
                    $"enabling {dependencyId} leaves the application pending without a package operation");
                await host.DisposeAsync();
                host = await TestHost.StartAsync(serverRoot, config);
                session.Url = host.Url;
                joint = Service();
                var activeClient = new ServerModuleManagementClient(factory.CreateClient("joint-fixture"), auth);
                var effective = (await activeClient.ReadAsync()).Single(module => module.Id == dependencyId);
                Require(effective.DesiredEnabled && effective.EffectiveEnabled && !effective.RestartRequired
                    && effective.RecoveryState == "none" && !applications.IsRegistered(new AppId(activationAppId)),
                    $"restart makes {dependencyId} effective before the desktop application is registered");
                var activationPending = await joint.FindPendingAsync(activationCandidate, default)
                    ?? throw new InvalidOperationException("Missing activation-only deployment after restart.");
                var activationInstalled = await joint.ExecuteAsync(activationPending, default);
                Require(activationInstalled.Id == activationAppId && applications.IsRegistered(new AppId(activationAppId)),
                    $"restart activates {dependencyId} and permits desktop application registration");
                await packages.UninstallAsync(activationAppId);
            }

            var proxyDependents = new List<string>();
            foreach (var bundledArchive in bundledModuleArchives)
            {
                var appId = Path.GetFileNameWithoutExtension(bundledArchive);
                string[] expectedModules = appId switch
                {
                    "relaxkonos.certificates" => [ServerModules.CertificatesId],
                    "relaxkonos.webservers" => [ServerModules.CertificatesId, ServerModules.WebServerId],
                    "relaxkonos.git" => [ServerModules.GitId],
                    "relaxkonos.firewall" => [ServerModules.FirewallId],
                    "relaxkonos.database" => [ServerModules.DatabaseId],
                    "relaxkonos.docker" => [ServerModules.DockerId],
                    "relaxkonos.tunnels" => ["relaxkonos.tunnels"],
                    "relaxkonos.tailscale" => ["relaxkonos.tailscale"],
                    "relaxkonos.proxy" or "relaxkonos.proxy-server" or "relaxkonos.squid" => [ServerModules.ProxyId],
                    _ => throw new ArgumentException($"Unrecognized bundled-module test package: {appId}"),
                };
                var bundledPath = Path.Combine(official, appId + ".roapp");
                File.Copy(bundledArchive, bundledPath, overwrite: true);
                File.Copy(bundledArchive + ".sha256", bundledPath + ".sha256", overwrite: true);
                var bundledCandidate = await installer.InspectLocalAsync(bundledPath);
                var bundledReview = await joint.PrepareAsync(bundledCandidate, [], default);
                var releases = bundledReview.Plan.OrderedReleases;
                Require(bundledReview.Plan.PreparedSteps.IsEmpty && releases.Length == expectedModules.Length + 1
                    && releases[^1].Package == new DeploymentPackageKey(DeploymentPackageKind.Application, appId)
                    && releases.Take(expectedModules.Length).All(item =>
                        item.Package.Kind == DeploymentPackageKind.ServerModule && item.Satisfied)
                    && releases.Take(expectedModules.Length).Select(item => item.Package.Id).SequenceEqual(expectedModules),
                    $"{appId} reuses every effective server dependency in dependency order");
                if (appId == "relaxkonos.git")
                {
                    const string version = RelaxKonOS.Protocol.Git.GitApiRoutes.CapabilityContractVersion;
                    Require(bundledCandidate.Manifest.Dependencies?.Required.Any(requirement =>
                            requirement.Capability == ServerCapabilities.Git && requirement.ContractVersion == version) == true
                        && releases[0].Capabilities.GetValueOrDefault(ServerCapabilities.Git) == version,
                        "the official Project Editor archive resolves the bundled Git capability contract");
                }
                var operationsBefore = ModulePackageOperationStore.Read(serverRoot).Count;
                var installedBundled = await joint.ExecuteAsync(bundledReview, default);
                Require(installedBundled.Id == appId && applications.IsRegistered(new AppId(appId))
                    && ModulePackageOperationStore.Read(serverRoot).Count == operationsBefore,
                    $"{appId} installs through the coordinator without mutating existing modules");
                if (expectedModules.Contains(ServerModules.ProxyId))
                {
                    proxyDependents.Add(appId);
                }
                else
                {
                    var directModules = appId == "relaxkonos.webservers" ? [ServerModules.WebServerId] : expectedModules;
                    var dependencies = directModules.Select(id => new DeploymentPackageKey(DeploymentPackageKind.ServerModule, id)).ToArray();
                    Require(dependencies.All(key => packages.RequiredBy(key).Contains(appId)),
                        $"{appId} is recorded as a dependent of each directly required module");
                    if (appId == "relaxkonos.webservers")
                    {
                        var moduleClient = new ServerModuleManagementClient(factory.CreateClient("joint-fixture"), auth);
                        var inventory = await moduleClient.ReadAsync();
                        var desired = inventory.Where(module => module.DesiredEnabled && module.Id != ServerModules.CertificatesId)
                            .Select(module => module.Id).Order(StringComparer.Ordinal).ToArray();
                        var disable = await moduleClient.PreviewAsync(new(serviceId, ServerModuleAction.Disable,
                            [ServerModules.CertificatesId], desired, inventory[0].DesiredRevision!, "verify-webserver-certificate-protection"));
                        Require(disable.BlockedBy.Any(blocker => blocker.ProblemCode == "module-dependency-blocked"
                            && blocker.ModuleId == ServerModules.CertificatesId),
                            "the server protects Certificates while Web Servers requires it");
                    }
                    await packages.UninstallAsync(appId);
                    Require(dependencies.All(key => !packages.RequiredBy(key).Contains(appId))
                        && ModulePackageOperationStore.Read(serverRoot).Count == operationsBefore,
                        $"uninstalling {appId} removes its dependency without changing the reused modules");
                }
            }

            var proxyModule = new DeploymentPackageKey(DeploymentPackageKind.ServerModule, ServerModules.ProxyId);
            if (proxyDependents.Count > 0)
            {
                Require(packages.RequiredBy(proxyModule).SequenceEqual(proxyDependents.Order(StringComparer.Ordinal)),
                    "every installed Proxy application protects the shared server module");
                foreach (var appId in proxyDependents)
                {
                    await packages.UninstallAsync(appId);
                    var remaining = proxyDependents.SkipWhile(item => item != appId).Skip(1).Order(StringComparer.Ordinal);
                    Require(packages.RequiredBy(proxyModule).SequenceEqual(remaining),
                        $"uninstalling {appId} preserves the shared dependency of the remaining applications");
                }
            }

            // Use the original desktop archive as a local prerequisite without a server dependency.
            File.Copy(archive, packagePath, overwrite: true);
            await using (var file = File.OpenRead(packagePath))
                await File.WriteAllTextAsync(packagePath + ".sha256", Convert.ToHexStringLower(await SHA256.HashDataAsync(file)));
            await installer.InstallAsync(await installer.InspectLocalAsync(packagePath));
            var offlineArchive = Path.Combine(root, "dependent.roapp");
            await PrepareDesktopArchiveAsync(archive, offlineArchive, "fixture.offline",
                new DeploymentPackageKey(DeploymentPackageKind.Application, AppIdText));
            session.Online = false;
            var requests = factory.Requests;
            var offlineCandidate = await installer.InspectLocalAsync(offlineArchive);
            var offline = await joint.PrepareAsync(offlineCandidate, [], default);
            var offlineApp = await joint.ExecuteAsync(offline, default);
            Require(offlineApp.Id == "fixture.offline" && factory.Requests == requests
                && offline.Plan.OrderedReleases[0].Satisfied,
                "a real desktop package with a satisfied client-only dependency installs offline without module API calls");
            using var availabilityServices = new ServiceCollection().AddSingleton(packages).AddSingleton(applications).BuildServiceProvider();
            using var availability = new ApplicationAvailabilityService(auth, availabilityServices, null!);
            Require(availability.GetCached(new AppId(offlineApp.Id)) is
                    { CanOpen: true, PackageKind: DeploymentPackageKind.Application, ServerIdentity: null }
                && factory.Requests == requests,
                "the offline desktop reports a satisfied local dependency without making a module API call");
            var original = packages.FindInstalled(AppIdText);
            await RewriteDesktopArchiveAsync(archive, packagePath, manifest => manifest["version"] = "2.0.0");
            var blocked = false;
            try { await installer.InstallAsync(await installer.InspectLocalAsync(packagePath)); }
            catch (InvalidOperationException error) when (error.Message.Contains("fixture.offline", StringComparison.Ordinal))
            {
                blocked = true;
            }
            Require(blocked && packages.FindInstalled(AppIdText) == original,
                "an incompatible replacement preserves the app version required by another installed app");
            await packages.UninstallAsync("fixture.offline");
            var upgraded = await installer.InstallAsync(await installer.InspectLocalAsync(packagePath));
            Require(upgraded.Version == "2.0.0", "removing the consuming app lets the same replacement proceed");
            await packages.UninstallAsync(AppIdText);

        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            if (Path.GetDirectoryName(root) == parent)
            {
                // Windows can retain the app's loaded package files until the process exits.
                try { Directory.Delete(root, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static Task PrepareDesktopArchiveAsync(string source, string path,
        string appId = AppIdText, DeploymentPackageKey? dependency = null, string? optionalModuleId = null,
        string? capability = null, string? contractVersion = null) =>
        RewriteDesktopArchiveAsync(source, path, manifest =>
        {
            var package = dependency ?? new DeploymentPackageKey(DeploymentPackageKind.ServerModule, ModuleId);
            manifest["id"] = appId;
            manifest["dependencies"] = new JsonObject
            {
                ["required"] = new JsonArray(new JsonObject
                {
                    ["package"] = new JsonObject { ["kind"] = (int)package.Kind, ["id"] = package.Id },
                    ["minimumVersion"] = "1.0.0",
                    ["maximumVersionExclusive"] = "2.0.0",
                }),
            };
            if (capability is not null)
            {
                var required = manifest["dependencies"]!["required"]![0]!;
                required["capability"] = capability;
                required["contractVersion"] = contractVersion;
            }
            if (optionalModuleId is not null)
                manifest["dependencies"]!["optional"] = new JsonArray(new JsonObject
                {
                    ["package"] = new JsonObject { ["kind"] = (int)DeploymentPackageKind.ServerModule, ["id"] = optionalModuleId },
                });
        });

    private static async Task RewriteDesktopArchiveAsync(string source, string path, Action<JsonNode> edit)
    {
        File.Copy(source, path, overwrite: true);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("manifest.json")!;
            JsonNode manifest;
            using (var input = entry.Open()) manifest = JsonNode.Parse(input)!;
            edit(manifest);
            entry.Delete();
            using var output = new StreamWriter(archive.CreateEntry("manifest.json").Open());
            await output.WriteAsync(manifest.ToJsonString());
        }
        await using var file = File.OpenRead(path);
        await File.WriteAllTextAsync(path + ".sha256", Convert.ToHexStringLower(await SHA256.HashDataAsync(file)));
    }

    private static async Task ExpectRestartAsync(JointAppDeploymentService service, JointAppDeploymentService.Review review)
    {
        try { await service.ExecuteAsync(review, default); }
        catch (InvalidOperationException error) when (error.Message == "deployment-module-AwaitingRestart") { return; }
        throw new InvalidOperationException("The installation should wait for a server restart.");
    }

    private static async Task ExpectInterruptedEnableAsync(JointAppDeploymentService service, JointAppDeploymentService.Review review)
    {
        try { await service.ExecuteAsync(review, default); }
        catch (HttpRequestException error) when (error.Message == "fixture-enable-apply-interrupted") { return; }
        throw new InvalidOperationException("Expected the enable request to be interrupted.");
    }

    private static void Require(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class CertificateFixturePrivilege : RelaxKonOS.Server.WebServer.IHostPrivilegeService
    {
        public bool IsAdministrator => true;
    }

    private sealed class CertificateFixtureElevation : IHostElevationSessionStore
    {
        public bool IsGranted(System.Security.Claims.ClaimsPrincipal principal, HostElevationCapability capability, string target) =>
            principal.FindFirst("sub")?.Value == ModuleTestAuthentication.Admin
            && capability == HostElevationCapability.CertificateIssue && target == "certificates/issue";

        public DateTimeOffset Grant(System.Security.Claims.ClaimsPrincipal principal, HostElevationCapability capability,
            string target, bool includeDescendants, string authenticationMethod, string? correlationId = null) =>
            throw new NotSupportedException();

        public void Revoke(System.Security.Claims.ClaimsPrincipal principal) => throw new NotSupportedException();
    }

    private sealed class ProxySubscriptionHttpFixture(byte[] body) : IHttpClientFactory, IProxySettingsReader
    {
        internal int RequestCount { get; private set; }

        public Task<ProxySettingsDto> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ProxySettingsReader.Defaults);

        public HttpClient CreateClient(string name) => name == "ProxySubscriptionDirect"
            ? new HttpClient(new ResponseHandler(this))
            : throw new InvalidOperationException("Unexpected subscription transport.");

        private sealed class ResponseHandler(ProxySubscriptionHttpFixture owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.RequestUri != new Uri("https://1.1.1.1/subscription"))
                    throw new InvalidOperationException("Unexpected subscription destination.");
                owner.RequestCount++;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(owner.responseBody) });
            }
        }

        private readonly byte[] responseBody = body;
    }

    private sealed class SquidPublicationHelper : IPrivilegedOperationTransport
    {
        public SquidPublicationReceipt? Receipt { get; set; }
        public SquidLifecycleReceipt? LifecycleReceipt { get; set; }
        public SquidAccountReceipt? AccountReceipt { get; set; }

        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Operation == PrivilegedOperationKind.SquidLifecycleReceipt)
                return Task.FromResult(new PrivilegedOperationResult(true,
                    OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(LifecycleReceipt))));
            if (request.Operation == PrivilegedOperationKind.SquidAccountReceipt)
                return Task.FromResult(new PrivilegedOperationResult(true,
                    OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(AccountReceipt))));
            if (request.Operation is not (PrivilegedOperationKind.SquidApplyConfiguration or PrivilegedOperationKind.SquidConfigurationReceipt))
                throw new InvalidOperationException($"Unexpected Squid fixture request: {request.Operation}");
            var rolledBack = request.Operation == PrivilegedOperationKind.SquidApplyConfiguration && Receipt is { Applied: false };
            return Task.FromResult(new PrivilegedOperationResult(!rolledBack,
                Error: rolledBack ? SquidGatewayService.RolledBack : null, SquidPublicationReceipt: Receipt));
        }
    }

    private sealed class SquidPublicationPaths(string stateDirectory) : IProxyPlatformPaths
    {
        public string GetStateDirectory() => stateDirectory;
        public string GetEngineVersionsDirectory(string engineId) => throw new NotSupportedException();
        public string GetEngineDataDirectory(string engineId) => throw new NotSupportedException();
        public string GetProtectedConfigurationDirectory() => throw new NotSupportedException();
        public string GetSanitizedLogDirectory() => throw new NotSupportedException();
    }

    private sealed class TailscaleReadHelper : IPrivilegedOperationTransport
    {
        internal static TailscaleControlSnapshot Snapshot { get; } = new(new string('a', 64), false, true, new(true, false), true);
        internal int ReadCount { get; private set; }

        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Operation != PrivilegedOperationKind.TailscaleControlRead || request.OperationId is not Guid id || id == Guid.Empty)
                throw new InvalidOperationException("Unexpected Tailscale Helper operation.");
            ReadCount++;
            return Task.FromResult(new PrivilegedOperationResult(true, TailscaleControl: Snapshot));
        }
    }

    private sealed class TestHost(WebApplication app, ModuleManagementStore store) : IAsyncDisposable
    {
        public string Url => app.Urls.Single();
        public IServiceProvider Services => app.Services;

        public static async Task<TestHost> StartAsync(string root, IConfiguration config)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = root, EnvironmentName = Environments.Development, Args = [],
            });
            builder.Logging.ClearProviders();
            builder.Configuration.AddConfiguration(config);
            var installed = InstalledModuleCatalog.Load(root);
            var store = new ModuleManagementStore(builder.Configuration, root, installed);
            var modules = ServerModules.CreateManaged(store.DesiredIds, installed);
            var online = installed.Where(module => modules.IsEnabled(module.Id)).ToArray();
            var hasTunnels = online.Any(module => module.Id == "relaxkonos.tunnels");
            var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, hasTunnels ? "sqlite" : "memory");
            if (hasTunnels)
            {
                builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(
                    new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
                builder.Services.AddSingleton<IServerModeResolver>(new ServerModeResolver(builder.Configuration,
                    RelaxKonOS.Server.UserExecution.UserExecutionBackend.Helper));
                builder.Services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(
                    new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
                builder.Services.AddDbContext<RelaxKonOSDbContext>(options =>
                    options.UseSqlite($"Data Source={Path.Combine(root, "tunnels-test.sqlite")};Pooling=False"));
                builder.Services.AddScoped<ISecretStore, DataProtectionSecretStore>();
                builder.Services.AddScoped<ITunnelAudit, TunnelAudit>();
                builder.Services.AddScoped<ITunnelService, TunnelService>();
                builder.Services.AddSingleton<ManagedRuntimeOperations>();
                builder.Services.AddSingleton<Func<RelaxKonOS.ServerModule.Tunnels.ITunnelExecution>>(services =>
                    () => services.GetRequiredService<RelaxKonOS.ServerModule.Tunnels.ITunnelExecution>());
                builder.Services.AddSingleton<ITunnelProvider, FrpTunnelProvider>();
                builder.Services.AddHostedService(services => (FrpTunnelProvider)services.GetRequiredService<ITunnelProvider>());
                builder.Services.AddSingleton(DispatchProxy.Create<IManagedFrpsService, GitRepositoryAssemblyChecks.RejectTransport>());
                builder.Services.AddSingleton<IRuntimeManager, FrpRuntimeManager>();
                builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new FrpRuntimeOptions()));
                builder.Services.AddSingleton(DispatchProxy.Create<RelaxKonOS.ManagedRuntimes.IPinnedArchiveTransfer, GitRepositoryAssemblyChecks.RejectTransport>());
                builder.Services.AddSingleton(DispatchProxy.Create<RelaxKonOS.Server.ManagedRuntimes.IRuntimeDownloadProxyProvider, GitRepositoryAssemblyChecks.RejectTransport>());
                builder.Services.AddSingleton(DispatchProxy.Create<IHostElevationSessionStore, GitRepositoryAssemblyChecks.RejectTransport>());
            }
            if (online.Any(module => module.Id == ServerModules.GitId))
            {
                builder.Services.AddSingleton(DispatchProxy.Create<RelaxKonOS.Server.Files.IFileService, GitRepositoryAssemblyChecks.RejectTransport>());
                ServerModules.CreateManaged(new HashSet<string> { ServerModules.GitId }, installed).ConfigureServices(builder, context);
                builder.Services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(
                    new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
                builder.Services.AddDbContextFactory<RelaxKonOSDbContext>(options =>
                    options.UseSqlite($"Data Source={Path.Combine(root, "git-test.sqlite")};Pooling=False"));
                builder.Services.AddSingleton(DispatchProxy.Create<IGitRepositoryHost, GitEngineProbeHost>());
            }
            var hasTailscale = online.Any(module => module.Id == "relaxkonos.tailscale");
            if (hasTailscale)
            {
                builder.Services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(
                    new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
                ServerModules.CreateManaged(new HashSet<string> { "relaxkonos.tailscale" }, installed).ConfigureServices(builder, context);
                if (!hasTunnels)
                    builder.Services.AddSingleton<IHostElevationSessionStore, NoElevation>();
            }
            var hasFirewall = online.Any(module => module.Id == ServerModules.FirewallId);
            if (hasFirewall)
            {
                builder.Services.AddSingleton<IServerModeResolver>(new ServerModeResolver(builder.Configuration,
                    RelaxKonOS.Server.UserExecution.UserExecutionBackend.Helper));
                builder.Services.AddSingleton<IHostElevationSessionStore, NoElevation>();
                ServerModules.CreateManaged(new HashSet<string> { ServerModules.FirewallId }, installed).ConfigureServices(builder, context);
            }
            foreach (var module in online.Where(module => module.Id is not (ServerModules.FirewallId or ServerModules.GitId or "relaxkonos.tailscale" or ServerModules.CertificatesId or ServerModules.DockerId or ServerModules.WebServerId or ServerModules.ProxyId)))
                module.ConfigureServices(builder, context);
            builder.Services.Configure<RelaxKonOS.Server.Docker.DockerComposeOptions>(options =>
                options.DataDirectory = Path.Combine(root, "docker-compose"));
            builder.Services.AddSingleton<RelaxKonOS.Server.Docker.DockerStackOperationStore>();
            var helperConfigPath = hasTailscale ? Environment.GetEnvironmentVariable("RK_TEST_TAILSCALE_HOST_CONFIG")
                : hasFirewall ? Environment.GetEnvironmentVariable("RK_TEST_FIREWALL_HOST_CONFIG") : null;
            if (helperConfigPath is { Length: > 0 })
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The installed Helper probe requires Windows.");
                using var installedConfig = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(helperConfigPath), optional: false).Build();
                var helperSection = installedConfig.GetRequiredSection("PrivilegedHelper");
                builder.Services.AddSingleton(new PrivilegedHelperOptions
                {
                    PipeName = helperSection["PipeName"] ?? string.Empty,
                    SharedSecret = helperSection["SharedSecret"] ?? string.Empty,
                });
                builder.Services.AddSingleton<IPrivilegedOperationTransport, WindowsNamedPipePrivilegedOperationTransport>();
            }
            else if (hasTailscale)
                builder.Services.AddSingleton<IPrivilegedOperationTransport, TailscaleReadHelper>();
            else if (hasFirewall)
                builder.Services.AddSingleton<IPrivilegedOperationTransport, FirewallResidencyChecks.FirewallTransport>();
            else
                builder.Services.AddSingleton<IPrivilegedOperationTransport, IdleDatabaseHelper>();
            builder.Services.AddSingleton(store);
            builder.Services.AddSingleton(modules);
            builder.Services.AddSingleton(context);
            builder.Services.AddSingleton<ModuleManagementExecutor>();
            builder.Services.AddHostedService(services => services.GetRequiredService<ModuleManagementExecutor>());
            builder.Services.AddAuthentication("module-test").AddScheme<AuthenticationSchemeOptions, ModuleTestAuthentication>("module-test", _ => { });
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy(ServerModuleManagementEndpoints.ManagePolicy,
                    policy => policy.RequireAuthenticatedUser().RequireAssertion(user => ServerModuleManagementEndpoints.CanManage(user.User)));
                AuthenticationServiceRegistration.AddTunnelPolicies(options);
            });
            WebApplication? app = null;
            try
            {
                app = builder.Build();
                if (online.Any(module => module.Id == ServerModules.GitId))
                {
                    await using var database = await app.Services.GetRequiredService<IDbContextFactory<RelaxKonOSDbContext>>().CreateDbContextAsync();
                    await database.Database.EnsureCreatedAsync();
                }
                if (hasTunnels)
                {
                    await using var scope = app.Services.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<RelaxKonOSDbContext>().Database.EnsureCreatedAsync();
                }
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapGet(ServerApiRoutes.Modules, async (HttpContext http, CancellationToken ct) => Results.Ok(
                    (await modules.DescribeAsync(http.RequestServices, context, ct))
                        .Select(module => ServerModuleManagementEndpoints.ForActor(module, http.User)))).RequireAuthorization();
                app.MapGet("/fixture-core", () => "ready");
                app.MapServerModuleManagementEndpoints();
                foreach (var module in online.Where(module => module.Id is not (ServerModules.CertificatesId or ServerModules.DockerId or ServerModules.WebServerId or ServerModules.ProxyId))) module.MapEndpoints(app, context);
                app.Urls.Add("http://127.0.0.1:0");
                await app.StartAsync();
                store.VerifyStartup(modules.EnabledIds);
                ModulePackageOperationStore.VerifyStartup(root, InstalledModuleStore.Read(root),
                    store.ProcessGeneration, DateTimeOffset.UtcNow);
                return new(app, store);
            }
            catch
            {
                try { if (app is not null) await app.DisposeAsync(); }
                finally { store.Dispose(); }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            store.Dispose();
        }
    }

    public class GitEngineProbeHost : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_UsesHelper" => false,
            "get_CurrentUserId" => null,
            "IsPathWithin" when args is [string root, string candidate] => RelaxKonOS.Server.PathSafety.IsWithin(root, candidate),
            _ => throw new NotSupportedException("Only local Git worktree operations are supported."),
        };
    }

    private sealed class FixtureHttpFactory(byte[] signedArchive) : IHttpClientFactory, IDisposable
    {
        private readonly ArchiveUploadHandler handler = new(signedArchive);
        private HttpClient? http;
        public IReadOnlyList<string> EnablePreviewKeys => handler.EnablePreviewKeys;
        public int SubmittedEnableApplies => handler.SubmittedEnableApplies;
        public int Requests => handler.Requests;
        public bool SubstituteReceiptRelease { get => handler.SubstituteReceiptRelease; set => handler.SubstituteReceiptRelease = value; }
        public bool SubstituteHealthVersion { get => handler.SubstituteHealthVersion; set => handler.SubstituteHealthVersion = value; }
        public bool SubstituteBlockedHealth { get => handler.SubstituteBlockedHealth; set => handler.SubstituteBlockedHealth = value; }
        public Func<bool>? BlockHealthWhen { set => handler.BlockHealthWhen = value; }
        public bool HideEffectiveVersion { get => handler.HideEffectiveVersion; set => handler.HideEffectiveVersion = value; }
        public bool SubstituteInventoryDigest { get => handler.SubstituteInventoryDigest; set => handler.SubstituteInventoryDigest = value; }
        public bool SubstituteFailedPackage { get => handler.SubstituteFailedPackage; set => handler.SubstituteFailedPackage = value; }
        public string? SubstituteServerIdentity { set => handler.SubstituteServerIdentity = value; }
        public bool SubstituteInstalledDigest { get => handler.SubstituteInstalledDigest; set => handler.SubstituteInstalledDigest = value; }
        public string? FaultedModule { get => handler.FaultedModule; set => handler.FaultedModule = value; }
        public bool SubstituteCapabilities { get => handler.SubstituteCapabilities; set => handler.SubstituteCapabilities = value; }
        public string ObservedCapabilityVersion { set => handler.ObservedCapabilityVersion = value; }
        public Action? BeforeNextClient { get; set; }
        public Func<Task>? BeforeNextRequest { set => handler.BeforeNextRequest = value; }
        public Task HoldNextEnableApply()
        {
            handler.EnableEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return handler.EnableEntered.Task;
        }
        public void DropNextEnableResponse() => handler.DropEnableResponse = true;
        public bool DropDependencyReply { set => handler.DropDependencyReply = value; }
        public bool BlockDependencyRelease { set => handler.BlockDependencyRelease = value; }
        public HttpClient CreateClient(string name)
        {
            var before = BeforeNextClient;
            BeforeNextClient = null;
            before?.Invoke();
            return http ??= new(handler);
        }
        public void Dispose() => http?.Dispose();
    }

    // Use the real server staging endpoint in place of the external HTTPS artifact host.
    private sealed class ArchiveUploadHandler(byte[] signedArchive) : DelegatingHandler(new HttpClientHandler { UseProxy = false })
    {
        public List<string> EnablePreviewKeys { get; } = [];
        public TaskCompletionSource? EnableEntered { get; set; }
        public bool DropEnableResponse { get; set; }
        public bool DropDependencyReply { get; set; }
        public bool BlockDependencyRelease { get; set; }
        public bool SubstituteReceiptRelease { get; set; }
        public bool SubstituteHealthVersion { get; set; }
        public bool SubstituteBlockedHealth { get; set; }
        public Func<bool>? BlockHealthWhen { get; set; }
        public bool HideEffectiveVersion { get; set; }
        public bool SubstituteInventoryDigest { get; set; }
        public bool SubstituteFailedPackage { get; set; }
        public string? SubstituteServerIdentity { get; set; }
        public bool SubstituteInstalledDigest { get; set; }
        public int SubmittedEnableApplies { get; private set; }
        public int Requests { get; private set; }
        public string? FaultedModule { get; set; }
        public bool SubstituteCapabilities { get; set; }
        public string ObservedCapabilityVersion { get; set; } = "2";
        public Func<Task>? BeforeNextRequest { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var before = BeforeNextRequest;
            BeforeNextRequest = null;
            if (before is not null) await before();
            request.Headers.Add("test-user", ModuleTestAuthentication.Admin);
            var path = request.RequestUri?.AbsolutePath;
            if (path?.StartsWith(ServerModuleManagementRoutes.DesktopDependencies + "/", StringComparison.Ordinal) == true)
            {
                if (request.Method == HttpMethod.Delete && BlockDependencyRelease)
                    throw new HttpRequestException("fixture-dependency-release-unreachable");
                if (request.Method == HttpMethod.Put && DropDependencyReply)
                {
                    DropDependencyReply = false;
                    using var applied = await base.SendAsync(request, cancellationToken);
                    applied.EnsureSuccessStatusCode();
                    throw new HttpRequestException("fixture-dependency-reply-lost");
                }
            }
            if (SubstituteInstalledDigest && request.Method == HttpMethod.Get && path == ServerModuleManagementRoutes.InstalledPackages)
            {
                using var response = await base.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var page = (await response.Content.ReadFromJsonAsync<ServerInstalledModulePageDto>(
                    RelaxKonOSJsonOptions.Default, cancellationToken))!;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(page with
                    {
                        Items = page.Items.Select(item => item.ModuleId == ModuleId
                            ? item with { ArtifactSha256 = new string('0', 64) } : item).ToArray(),
                    }, options: RelaxKonOSJsonOptions.Default),
                };
            }
            var blockedHealth = request.Method == HttpMethod.Get && path == ServerModuleManagementRoutes.ModuleHealth
                && (SubstituteBlockedHealth || BlockHealthWhen?.Invoke() == true);
            if ((SubstituteHealthVersion || blockedHealth)
                && request.Method == HttpMethod.Get && path == ServerModuleManagementRoutes.ModuleHealth)
            {
                using var response = await base.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var report = (await response.Content.ReadFromJsonAsync<ServerModuleHealthPageDto>(
                    RelaxKonOSJsonOptions.Default, cancellationToken))!;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(report with
                    {
                        Healthy = report.Healthy && !blockedHealth,
                        Modules = report.Modules.Select(item => item.ModuleId != ModuleId ? item
                            : blockedHealth ? item with
                            {
                                Status = "blocked", ProblemCode = "module-host-code-trust-required", Version = null,
                            }
                            : item with { Version = "9.0.0" }).ToArray(),
                    }, options: RelaxKonOSJsonOptions.Default),
                };
            }
            if ((SubstituteReceiptRelease || SubstituteFailedPackage) && request.Method == HttpMethod.Get
                && path?.StartsWith(ServerModuleManagementRoutes.PackageOperations + "/", StringComparison.Ordinal) == true)
            {
                using var response = await base.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var receipt = await response.Content.ReadFromJsonAsync<ServerPackageOperationDto>(
                    RelaxKonOSJsonOptions.Default, cancellationToken);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(SubstituteFailedPackage
                        ? receipt! with { State = "Failed", ProblemCode = "fixture-package-failure" }
                        : receipt! with { ReleaseId = "other-release" }, options: RelaxKonOSJsonOptions.Default),
                };
            }
            if (request.Method == HttpMethod.Get && path == ServerApiRoutes.Modules
                && (FaultedModule is not null || HideEffectiveVersion || SubstituteCapabilities || SubstituteInventoryDigest || SubstituteServerIdentity is not null))
            {
                using var response = await base.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var modules = await response.Content.ReadFromJsonAsync<ServerModuleDto[]>(RelaxKonOSJsonOptions.Default, cancellationToken);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(modules!.Select(module => SubstituteServerIdentity is { } identity
                        ? module with { ServiceId = identity }
                        : module.Id == FaultedModule
                        ? module with { Residency = new("faulted", false, "KeepLoaded", 0, 0, null, null, null, "fixture", null) }
                        : HideEffectiveVersion && module.Id == ModuleId ? module with { EffectiveVersion = null }
                        : SubstituteCapabilities && module.Id == ModuleId ? module with { Capabilities = ["fixture.changed"], CapabilityContractVersions =
                            new Dictionary<string, string> { ["fixture.changed"] = ObservedCapabilityVersion } }
                        : SubstituteInventoryDigest && module.Id == ModuleId ? module with { ArtifactDigest = new string('0', 64) }
                        : module).ToArray(), options: RelaxKonOSJsonOptions.Default),
                };
            }
            if (request.Method == HttpMethod.Post && path == ServerModuleManagementRoutes.Plans)
            {
                var plan = await request.Content!.ReadFromJsonAsync<ServerModulePlanRequest>(
                    RelaxKonOSJsonOptions.Default, cancellationToken);
                if (plan?.Action == ServerModuleAction.Enable) EnablePreviewKeys.Add(plan.IdempotencyKey);
            }
            if (request.Method == HttpMethod.Post
                && path?.StartsWith(ServerModuleManagementRoutes.Plans + "/", StringComparison.Ordinal) == true
                && path.EndsWith("/apply", StringComparison.Ordinal))
            {
                if (EnableEntered is { } entered)
                {
                    EnableEntered = null;
                    entered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                var drop = DropEnableResponse;
                DropEnableResponse = false;
                SubmittedEnableApplies++;
                var applied = await base.SendAsync(request, cancellationToken);
                if (!drop) return applied;
                using (applied) applied.EnsureSuccessStatusCode();
                throw new HttpRequestException("fixture-enable-apply-interrupted");
            }
            if (request.RequestUri?.AbsolutePath != ServerModuleManagementRoutes.ModuleStoreDownload)
                return await base.SendAsync(request, cancellationToken);
            using var upload = new HttpRequestMessage(HttpMethod.Post, new Uri(request.RequestUri, ServerModuleManagementRoutes.PackageUpload));
            upload.Headers.Add("test-user", ModuleTestAuthentication.Admin);
            upload.Content = new ByteArrayContent(signedArchive);
            return await base.SendAsync(upload, cancellationToken);
        }
    }

    public class SessionProxy : DispatchProxy
    {
        public Guid AccountId { get; set; } = Guid.Parse(ModuleTestAuthentication.Admin);
        private SessionDto login = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default, default,
            SessionStatus.Active, Guid.Parse(ModuleTestAuthentication.Admin), "test", default);
        private string url = "";
        public bool Online { get; set; } = true;
        public bool KeepDisconnectedIdentity { get; set; }
        public string ConnectionIdentity { get; set; } = "local-joint-test";
        public string Url
        {
            get => url;
            set
            {
                url = value;
                login = login with { Id = Guid.NewGuid() };
            }
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_State" => Online ? AuthSessionState.Authenticated : AuthSessionState.Unauthenticated,
            "get_ServiceId" => Online || KeepDisconnectedIdentity ? ConnectionIdentity : null,
            "get_EffectiveBaseUrl" => Url,
            "get_CurrentSession" => Online || KeepDisconnectedIdentity ? login : null,
            "get_CurrentUser" => Online || KeepDisconnectedIdentity
                ? new UserDto(AccountId, "controller", HostPlatformKind.Windows, "fixture", default, null) : null,
            "get_CurrentServer" => null,
            "add_StateChanged" or "remove_StateChanged" => null,
            _ => throw new NotSupportedException(method?.Name),
        };
    }

    public class PermissionProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == "Clear" ? null : throw new NotSupportedException(method?.Name);
    }

    private sealed class Diagnostics : RelaxKonOS.AppSDK.IAppActivationDiagnostics
    {
        public void Record(string message) => Console.WriteLine(message);
    }
}
