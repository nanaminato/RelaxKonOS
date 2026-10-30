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
        // 任务管理器/系统性能的采集链路由 system-performance 模块所有（含后台采样与 PerformanceHub），
        // 见 Startup/Modules/SystemPerformanceModule.cs；此处不再重复注册。

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
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerRuntimeInstaller, RelaxKonOS.Server.Docker.DockerRuntimeInstaller>();
        // Engine access and the host proxy preference are shared infrastructure, not part of the
        // Docker manager. Application deployment, the Docker runtime installer, Nginx/Mihomo/FRP
        // downloads and the managed-runtime download path all read them, so they stay in Core:
        // disabling Modules:Docker must never send a managed-runtime download direct by accident.
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerProxyResolver, RelaxKonOS.Server.Docker.DockerProxyResolver>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IOutboundProxyHttpClientFactory, RelaxKonOS.Server.Docker.OutboundProxyHttpClientFactory>();
        // Managed-runtime installs read the same host preference, so an operator who routed runtime
        // downloads through a proxy for Nginx/FRP is not silently sent direct by the newer lifecycle.
        // The resolver is looked up per operation: its own dependency chain runs through the proxy
        // settings and publication services, which are built after this runtime service.
        builder.Services.AddSingleton<RelaxKonOS.Server.ManagedRuntimes.IRuntimeDownloadProxyProvider>(services =>
            new RelaxKonOS.Server.ManagedRuntimes.RuntimeDownloadProxyProvider(
                () => services.GetRequiredService<RelaxKonOS.Server.Docker.IDockerProxyResolver>()));
        // 容器/镜像/网络/卷编辑、compose 与栈操作协调器、引擎生命周期与 Docker 代理界面归 docker 模块所有
        // （见 Startup/Modules/DockerModule.cs）；它们不可用不影响部署与组件下载使用引擎。
        // The stack operation ledger is the one exception: it is durable state with a recovery duty, and
        // the deactivation gate has to read it while the manager is disabled. The coordinator that
        // writes to it and reconciles it stays in the module.
        builder.Services.Configure<RelaxKonOS.Server.Docker.DockerComposeOptions>(builder.Configuration.GetSection("DockerCompose"));
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.DockerStackOperationStore>();
        var guardianOptions = builder.Configuration.GetSection("GuardianAgent").Get<RelaxKonOS.Server.ProcessGuardian.GuardianAgentOptions>() ?? new RelaxKonOS.Server.ProcessGuardian.GuardianAgentOptions();
        builder.Services.AddSingleton(guardianOptions);
        builder.Services.AddSingleton<RelaxKonOS.Server.ProcessGuardian.IProcessGuardianService, RelaxKonOS.Server.ProcessGuardian.NamedPipeProcessGuardianService>();
        builder.Services.AddScoped<RelaxKonOS.Server.ProcessGuardian.IRunAsAuthorizationService, RelaxKonOS.Server.ProcessGuardian.RunAsAuthorizationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ProcessGuardian.IGuardianAgentInstaller, RelaxKonOS.Server.ProcessGuardian.GuardianAgentInstaller>();
        builder.Services.AddSingleton(builder.Configuration.GetSection("GuardianNativeServices").Get<RelaxKonOS.Server.ProcessGuardian.NativeServiceAdapterOptions>() ?? new RelaxKonOS.Server.ProcessGuardian.NativeServiceAdapterOptions());
        builder.Services.AddSingleton<RelaxKonOS.Server.ProcessGuardian.INativeServiceAdapter, RelaxKonOS.Server.ProcessGuardian.NativeServiceAdapter>();

        // Git 客户端与源码构建归 git 模块所有（见 Startup/Modules/GitModule.cs）；部署域只通过
        // IDeploymentImageBuildSource 端口询问「某次构建是否可部署」。

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
        builder.Services.AddSingleton<RelaxKonOS.Server.Components.IComponentLifecycleAdapter, RelaxKonOS.Server.Components.TailscaleComponentAdapter>();
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
        // 网站发布事务归 webserver 模块（见 Startup/Modules/WebServerModule.cs）：它依赖证书模块，
        // 而 Nginx 的读状态与集成操作仍留在 Core，供部署与组件中心使用。

        // Tunnel desired state is stored separately from workspace preferences. FRP stays an external
        // process; the provider only generates private configuration and supervises its own child PID.
        builder.Services.AddSingleton<RelaxKonOS.Server.Runtimes.IRuntimeManager, RelaxKonOS.Server.Runtimes.FrpRuntimeManager>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Tunnels.ITunnelProvider, RelaxKonOS.Server.Tunnels.FrpTunnelProvider>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Tunnels.IManagedFrpsService, RelaxKonOS.Server.Tunnels.ManagedFrpsService>();
        builder.Services.AddHostedService(sp => (RelaxKonOS.Server.Tunnels.FrpTunnelProvider)sp.GetRequiredService<RelaxKonOS.Server.Tunnels.ITunnelProvider>());
        builder.Services.AddSingleton<RelaxKonOS.ManagedRuntimes.IPinnedArchiveTransfer, RelaxKonOS.ManagedRuntimes.RuntimePinnedArchiveTransfer>();
        builder.Services.Configure<RelaxKonOS.Server.Runtimes.FrpRuntimeOptions>(options =>
        {
            options.BindConfiguration(builder.Configuration.GetSection("FrpRuntime"));
        });

        // Certificate storage and the Kestrel boot path are host identity, not a management surface:
        // the TLS listener must start even when the certificate manager module is disabled. Only the
        // ACME/renewal/management layer belongs to relaxkonos.certificates.
        var certificateOptions = builder.Configuration.GetSection("Certificate").Get<RelaxKonOS.Server.Certificate.CertificateOptions>() ?? new RelaxKonOS.Server.Certificate.CertificateOptions();
        builder.Services.AddSingleton(certificateOptions);
        builder.Services.AddSingleton(kestrelCertificates);
        builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.FileHttp01ChallengeStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.CertificateMetadataRepository>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.ICertificateStore, RelaxKonOS.Server.Certificate.FileCertificateStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.CertificateDeploymentRepository>();
        builder.Services.AddHostedService<RelaxKonOS.Server.Certificate.KestrelCertificateStartupService>();
        // ACME、续期与证书管理端点归 certificates 模块所有（见 Startup/Modules/CertificateModule.cs）。
    }
}
