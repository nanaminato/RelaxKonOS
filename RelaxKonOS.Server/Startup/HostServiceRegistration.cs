using System.Runtime.InteropServices;

namespace RelaxKonOS.Server.Startup;

internal static class HostServiceRegistration
{
    public static void AddHostServices(this WebApplicationBuilder builder, RelaxKonOS.Server.Certificate.KestrelCertificateRegistry kestrelCertificates)
    {
        builder.Services.AddSingleton<RelaxKonOS.Server.ManagedRuntimes.RuntimeCatalogService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.TaskOrchestration.IRuntimeTaskPreviewService,
            RelaxKonOS.Server.TaskOrchestration.RuntimeTaskPreviewService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.TaskOrchestration.TaskPlanStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.TaskOrchestration.ITaskRuntimeHostObserver,
            RelaxKonOS.Server.TaskOrchestration.SingBoxTaskRuntimeHostObserver>();
        builder.Services.AddSingleton<RelaxKonOS.Server.TaskOrchestration.ITaskApprovalPolicyReader,
            RelaxKonOS.Server.TaskOrchestration.TaskApprovalPolicyReader>();
        builder.Services.AddSingleton<RelaxKonOS.Server.TaskOrchestration.ITaskExecutionPolicyReader,
            RelaxKonOS.Server.TaskOrchestration.TaskApprovalPolicyReader>();
        builder.Services.AddSingleton<RelaxKonOS.Server.TaskOrchestration.ITaskApproverPolicyReader,
            RelaxKonOS.Server.TaskOrchestration.TaskApproverPolicyReader>();
        builder.Services.AddSingleton<RelaxKonOS.Server.TaskOrchestration.TaskStepAuthorizationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.TaskOrchestration.TaskDispatchService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.TaskOrchestration.ITaskStepDispatcher>(services =>
            services.GetRequiredService<RelaxKonOS.Server.TaskOrchestration.TaskDispatchService>());
        builder.Services.AddSingleton<RelaxKonOS.Server.TaskOrchestration.TaskRunAdvancementService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.TaskOrchestration.TaskRunCancellationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.TaskOrchestration.ITaskPlanRevalidator,
            RelaxKonOS.Server.TaskOrchestration.RuntimeTaskPlanRevalidator>();
        builder.Services.AddScoped<RelaxKonOS.Server.TaskOrchestration.TaskPlanApprovalService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.TaskOrchestration.TaskRunStartService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.TaskOrchestration.ITaskReconciliationDomain,
            RelaxKonOS.Server.TaskOrchestration.SingBoxTaskReconciliationDomain>();
        builder.Services.AddSingleton<RelaxKonOS.Server.TaskOrchestration.TaskRunReconciliationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.TaskOrchestration.TaskRecoveryReconciliationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.TaskOrchestration.ITaskRecoveryDomain, RelaxKonOS.Server.TaskOrchestration.SingBoxTaskRecoveryDomain>();
        builder.Services.AddScoped<RelaxKonOS.Server.TaskOrchestration.TaskRecoveryService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.TaskOrchestration.ITaskRunReconciler>(services =>
            services.GetRequiredService<RelaxKonOS.Server.TaskOrchestration.TaskRunReconciliationService>());
        builder.Services.AddSingleton<RelaxKonOS.Server.TaskOrchestration.IRuntimeTaskChangePlanningService,
            RelaxKonOS.Server.TaskOrchestration.RuntimeTaskChangePlanningService>();
        // 任务管理器：系统指标采集 Provider（按宿主 OS 平台选择，与 IIdentityProvider 同模式）。
        // CPU/内存平台特定（Linux 读 /proc；Windows 走 P/Invoke），磁盘/网络/GPU/进程跨平台共享。
        // 以宿主 OS 进程身份读取，复用宿主用户/权限（不另建 ACL）。Singleton 持相邻采样差分状态。
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            builder.Services.AddSingleton<RelaxKonOS.Server.SystemMonitor.ISystemMetricsProvider, RelaxKonOS.Server.SystemMonitor.WindowsMetricsProvider>();
        else
            builder.Services.AddSingleton<RelaxKonOS.Server.SystemMonitor.ISystemMetricsProvider, RelaxKonOS.Server.SystemMonitor.LinuxMetricsProvider>();

        // 新任务管理器性能链路：订阅期间统一 1 秒读取原始 OS 数据，短期历史和 Hub 广播各自分层。
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            builder.Services.AddSingleton<RelaxKonOS.Server.SystemPerformance.ISystemPerformanceSource, RelaxKonOS.Server.SystemPerformance.WindowsPerformanceSource>();
        else
            builder.Services.AddSingleton<RelaxKonOS.Server.SystemPerformance.ISystemPerformanceSource, RelaxKonOS.Server.SystemPerformance.LinuxPerformanceSource>();
        builder.Services.AddSingleton<RelaxKonOS.Server.SystemPerformance.PerformanceHistory>();
        builder.Services.AddSingleton<RelaxKonOS.Server.SystemPerformance.PerformanceSubscriptionRegistry>();
        builder.Services.AddSingleton<RelaxKonOS.Server.SystemPerformance.PerformanceSampler>();
        builder.Services.AddSingleton<RelaxKonOS.Server.SystemPerformance.IPerformanceSampler>(sp => sp.GetRequiredService<RelaxKonOS.Server.SystemPerformance.PerformanceSampler>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RelaxKonOS.Server.SystemPerformance.PerformanceSampler>());
        builder.Services.AddSingleton<RelaxKonOS.Server.SystemPerformance.ProcessSampler>();
        builder.Services.AddSingleton<RelaxKonOS.Server.SystemPerformance.IProcessService>(sp => sp.GetRequiredService<RelaxKonOS.Server.SystemPerformance.ProcessSampler>());

        // Built-in Docker manager: the provider uses Docker's local CLI transport only; no socket/pipe
        // is ever exposed to clients. Guardian intentionally remains a separate Agent boundary.
        // The application-deployment domain generates its build contexts under its own root, so that root
        // is approved here too. Without it every archive-source build is rejected before the Docker CLI is
        // ever invoked, and the failure only surfaces later as a build problem. Deriving the path from the
        // deployment options keeps a single source of truth for where build contexts live.
        builder.Services.AddSingleton(sp =>
        {
            var configured = sp.GetRequiredService<IConfiguration>().GetSection("DockerEngine")
                .Get<RelaxKonOS.Server.Docker.DockerCliEngineOptions>() ?? new RelaxKonOS.Server.Docker.DockerCliEngineOptions();
            var deploymentBuildRoot = Path.Combine(
                builder.Environment.ContentRootPath,
                sp.GetRequiredService<RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentOptions>().RootDirectory,
                "build");
            return configured with { BuildRoots = [.. configured.BuildRoots, deploymentBuildRoot] };
        });
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerEngineService, RelaxKonOS.Server.Docker.DockerCliEngineService>();
        builder.Services.AddScoped<RelaxKonOS.Server.ImageMirrors.IDockerImageMirrorResolver, RelaxKonOS.Server.ImageMirrors.DockerImageMirrorResolver>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerRuntimeInstaller, RelaxKonOS.Server.Docker.DockerRuntimeInstaller>();
        builder.Services.Configure<RelaxKonOS.Server.Docker.DockerComposeOptions>(builder.Configuration.GetSection("DockerCompose"));
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerComposeService, RelaxKonOS.Server.Docker.DockerComposeService>();
        // Compose stack operations are durable: the ledger outlives the HTTP request, the phone that started
        // it, and this process. The coordinator is the only caller of the executor and reconciles an
        // interrupted operation against the real Engine on startup instead of replaying it.
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.DockerStackOperationStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.DockerStackOperationCoordinator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RelaxKonOS.Server.Docker.DockerStackOperationCoordinator>());
        // Docker proxy: one resolver feeds both the docker child-process environment and the daemon
        // configurator, so the two layers can never disagree about the saved preference. The configurator
        // owns the platform difference internally (systemd drop-in on Linux, Docker Desktop settings on
        // Windows) and reports Unsupported elsewhere.
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerEngineProxyConfigurator, RelaxKonOS.Server.Docker.DockerEngineProxyConfigurator>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerProxyResolver, RelaxKonOS.Server.Docker.DockerProxyResolver>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IOutboundProxyHttpClientFactory, RelaxKonOS.Server.Docker.OutboundProxyHttpClientFactory>();
        // Managed-runtime installs read the same host preference, so an operator who routed runtime
        // downloads through a proxy for Nginx/FRP is not silently sent direct by the newer lifecycle.
        // The resolver is looked up per operation: its own dependency chain runs through the proxy
        // settings and publication services, which are built after this runtime service.
        builder.Services.AddSingleton<RelaxKonOS.Server.ManagedRuntimes.IRuntimeDownloadProxyProvider>(services =>
            new RelaxKonOS.Server.ManagedRuntimes.RuntimeDownloadProxyProvider(
                () => services.GetRequiredService<RelaxKonOS.Server.Docker.IDockerProxyResolver>()));
        // Docker Desktop is the only host that hides the real upstream behind an internal relay, so the
        // reader that knows its settings file is selected here instead of probing for a file that cannot
        // exist on the other platforms.
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerDesktopProxyReader>(_ =>
            OperatingSystem.IsWindows()
                ? new RelaxKonOS.Server.Docker.WindowsDockerDesktopProxyReader()
                : new RelaxKonOS.Server.Docker.UnsupportedDockerDesktopProxyReader());
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerProxyService, RelaxKonOS.Server.Docker.DockerProxyService>();
        // Engine lifecycle control reuses the same platform split as the proxy configurator: a systemd
        // unit driven through the privileged Helper, or the Docker Desktop CLI.
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerEngineHostController, RelaxKonOS.Server.Docker.DockerEngineHostController>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerEngineControlService, RelaxKonOS.Server.Docker.DockerEngineControlService>();
        var guardianOptions = builder.Configuration.GetSection("GuardianAgent").Get<RelaxKonOS.Server.ProcessGuardian.GuardianAgentOptions>() ?? new RelaxKonOS.Server.ProcessGuardian.GuardianAgentOptions();
        builder.Services.AddSingleton(guardianOptions);
        builder.Services.AddSingleton<RelaxKonOS.Server.ProcessGuardian.IProcessGuardianService, RelaxKonOS.Server.ProcessGuardian.NamedPipeProcessGuardianService>();
        builder.Services.AddScoped<RelaxKonOS.Server.ProcessGuardian.IRunAsAuthorizationService, RelaxKonOS.Server.ProcessGuardian.RunAsAuthorizationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ProcessGuardian.IGuardianAgentInstaller, RelaxKonOS.Server.ProcessGuardian.GuardianAgentInstaller>();
        builder.Services.AddSingleton(builder.Configuration.GetSection("GuardianNativeServices").Get<RelaxKonOS.Server.ProcessGuardian.NativeServiceAdapterOptions>() ?? new RelaxKonOS.Server.ProcessGuardian.NativeServiceAdapterOptions());
        builder.Services.AddSingleton<RelaxKonOS.Server.ProcessGuardian.INativeServiceAdapter, RelaxKonOS.Server.ProcessGuardian.NativeServiceAdapter>();

        // Git client: server-side git CLI service (Singleton—holds per-repo write semaphore).
        // Invokes host git CLI as the host user; credentials handled entirely by the host git credential helper.
        builder.Services.AddSingleton<RelaxKonOS.Server.Git.IHostGitCli, RelaxKonOS.Server.Git.HostGitCli>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Git.IGitRepositoryService, RelaxKonOS.Server.Git.LocalGitRepositoryService>();
        builder.Services.AddScoped<RelaxKonOS.Server.Git.GitTextEditor>();
        builder.Services.AddSingleton(builder.Configuration.GetSection("GitBuilds").Get<RelaxKonOS.Server.Git.GitBuildOptions>()
            ?? new RelaxKonOS.Server.Git.GitBuildOptions());
        builder.Services.AddSingleton<RelaxKonOS.Server.Git.GitBuildService>();

        // Firewall uses structured UFW or Windows Defender operations through the privileged Helper.
        if (OperatingSystem.IsLinux())
            builder.Services.AddSingleton<RelaxKonOS.Server.Firewall.IHostFirewallService, RelaxKonOS.Server.Firewall.LinuxUfwFirewallService>();
        else if (OperatingSystem.IsWindows())
            builder.Services.AddSingleton<RelaxKonOS.Server.Firewall.IHostFirewallService, RelaxKonOS.Server.Firewall.WindowsFirewallService>();
        else
            builder.Services.AddSingleton<RelaxKonOS.Server.Firewall.IHostFirewallService, RelaxKonOS.Server.Firewall.UnavailableHostFirewallService>();

        // Web Server V1: host-global Nginx discovery/read state plus an explicitly confirmed,
        // marker-owned conf.d integration. It never accepts shell text or elevation credentials from HTTP.
        builder.Services.AddSingleton<RelaxKonOS.Server.WebServer.IHostPrivilegeService, RelaxKonOS.Server.WebServer.HostPrivilegeService>();
        var nginxManagedOptions = builder.Configuration.GetSection("NginxManaged").Get<RelaxKonOS.Server.WebServer.NginxManagedOptions>() ?? new();
        if (OperatingSystem.IsLinux() && builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(nginxManagedOptions.InstallationRoot))
            nginxManagedOptions = new() { InstallationRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "RelaxKonOS", "debug", "webserver", "nginx") };
        builder.Services.AddSingleton(nginxManagedOptions);
        builder.Services.AddSingleton<RelaxKonOS.Server.WebServer.NginxInstallPackageStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.HostOperationJournal>();
        builder.Services.AddSingleton<RelaxKonOS.Server.WebServer.WebServerMetadataRepository>();
        builder.Services.AddSingleton<RelaxKonOS.Server.WebServer.WebServerOperationStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.WebServer.NginxWebServerManager>();
        builder.Services.AddSingleton<RelaxKonOS.Server.WebServer.IWebServerProvider>(services => services.GetRequiredService<RelaxKonOS.Server.WebServer.NginxWebServerManager>());
        builder.Services.AddSingleton<RelaxKonOS.Server.WebServer.IWebServerManager, RelaxKonOS.Server.WebServer.WebServerManager>();
        // Component lifecycle adapters: one closed registry decides which components the center may
        // answer for. Each adapter normalizes its own product (ledgered archive vs distribution
        // package) so callers never branch on the product.
        builder.Services.AddSingleton<RelaxKonOS.Server.Components.IComponentLifecycleAdapter, RelaxKonOS.Server.Components.SingBoxComponentAdapter>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Components.IComponentLifecycleAdapter, RelaxKonOS.Server.Components.NginxComponentAdapter>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Components.IComponentLifecycleAdapter, RelaxKonOS.Server.Components.MihomoComponentAdapter>();
        foreach (var componentId in new[] { "squid", "samba", "docker", "git" })
            builder.Services.AddSingleton<RelaxKonOS.Server.Components.IComponentLifecycleAdapter>(services =>
                new RelaxKonOS.Server.Components.HostPackageComponentAdapter(componentId,
                    services.GetRequiredService<RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport>()));
        foreach (var componentId in new[] { "frpc", "frps" })
            builder.Services.AddSingleton<RelaxKonOS.Server.Components.IComponentLifecycleAdapter>(services =>
                new RelaxKonOS.Server.Components.FrpComponentAdapter(componentId,
                    services.GetRequiredService<RelaxKonOS.Server.Runtimes.IRuntimeManager>(),
                    services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RelaxKonOS.Server.Runtimes.FrpRuntimeOptions>>(),
                    services.GetRequiredService<RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport>()));
        builder.Services.AddSingleton<RelaxKonOS.Server.Components.ComponentLifecycleRegistry>();
        builder.Services.AddSingleton<RelaxKonOS.Server.WebsitePublishing.WebsitePublicationStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.WebsitePublishing.WebsitePublicationCoordinator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RelaxKonOS.Server.WebsitePublishing.WebsitePublicationCoordinator>());
        builder.Services.AddHttpClient("WebsitePublicationVerification", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        // Tunnel desired state is stored separately from workspace preferences. FRP stays an external
        // process; the provider only generates private configuration and supervises its own child PID.
        builder.Services.AddSingleton<RelaxKonOS.Server.Runtimes.IRuntimeManager, RelaxKonOS.Server.Runtimes.FrpRuntimeManager>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Tunnels.ITunnelProvider, RelaxKonOS.Server.Tunnels.FrpTunnelProvider>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Tunnels.IManagedFrpsService, RelaxKonOS.Server.Tunnels.ManagedFrpsService>();
        builder.Services.AddHostedService(sp => (RelaxKonOS.Server.Tunnels.FrpTunnelProvider)sp.GetRequiredService<RelaxKonOS.Server.Tunnels.ITunnelProvider>());
        builder.Services.AddSingleton<RelaxKonOS.ManagedRuntimes.IPinnedArchiveTransfer, RelaxKonOS.ManagedRuntimes.RuntimePinnedArchiveTransfer>();
        builder.Services.Configure<RelaxKonOS.Server.Runtimes.FrpRuntimeOptions>(builder.Configuration.GetSection("FrpRuntime"));

        // Certificate management is host-global. PEM/account keys remain behind the server-side
        // store; the API exposes metadata and operation IDs only.
        var certificateOptions = builder.Configuration.GetSection("Certificate").Get<RelaxKonOS.Server.Certificate.CertificateOptions>() ?? new RelaxKonOS.Server.Certificate.CertificateOptions();
        builder.Services.AddSingleton(certificateOptions);
        builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.FileHttp01ChallengeStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.DirectHttp01ChallengeStore>();
        builder.Services.AddSingleton(kestrelCertificates);
        builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.CertificateMetadataRepository>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.ICertificateStore, RelaxKonOS.Server.Certificate.FileCertificateStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.CertificateDeploymentRepository>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.IAcmeService, RelaxKonOS.Server.Certificate.AnvilAcmeService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.IAcmeRenewalInfoProvider>(services => (RelaxKonOS.Server.Certificate.AnvilAcmeService)services.GetRequiredService<RelaxKonOS.Server.Certificate.IAcmeService>());
        builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.CertificateRenewalAttemptRepository>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.CertificateOperationStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.CertificateRenewalRunRepository>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.ICertificateManager, RelaxKonOS.Server.Certificate.CertificateManager>();
        builder.Services.AddHostedService<RelaxKonOS.Server.Certificate.KestrelCertificateStartupService>();
        builder.Services.AddHostedService<RelaxKonOS.Server.Certificate.CertificateRenewalWorker>();
    }
}
