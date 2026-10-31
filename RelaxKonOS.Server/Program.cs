using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Secrets;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Storage;

if (args.FirstOrDefault() == "auth") { Environment.ExitCode = await AuthMaintenanceCommand.RunAsync(args); return; }
if (args.FirstOrDefault() == "modules") { Environment.ExitCode = await ModuleMaintenanceCommand.RunAsync(args); return; }
if (args.FirstOrDefault() == "guardian-health") { Environment.ExitCode = await RelaxKonOS.Server.ProcessGuardian.GuardianHealthCommand.RunAsync(args); return; }

// `dotnet run` normally treats the project directory as ContentRoot, which would put every
// ContentRoot-relative runtime artifact under the checkout's data directory. Keep development
// data beside the compiled executable instead, while installed hosts retain their configured
// content root and storage locations.
var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
    ?? Environments.Production;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    EnvironmentName = environmentName,
    ContentRootPath = environmentName.Equals(Environments.Development, StringComparison.OrdinalIgnoreCase)
        ? AppContext.BaseDirectory
        : null,
});
var isDevelopment = environmentName.Equals(Environments.Development, StringComparison.OrdinalIgnoreCase);
// The installer writes this optional host configuration before the mode boundary is evaluated.
// It must not be possible for a later configuration provider to change an already-validated mode.
builder.Configuration.AddJsonFile("appsettings.host.json", optional: true, reloadOnChange: false);
ModulePackageStartupGuard.Begin(builder.Environment.ContentRootPath, Guid.NewGuid().ToString("N"));
var installedModules = InstalledModuleCatalog.Load(builder.Environment.ContentRootPath);
using var moduleManagement = new ModuleManagementStore(builder.Configuration, builder.Environment.ContentRootPath, installedModules);
if (moduleManagement.RecoveryMode)
{
    await using var recoveryHost = await ModuleRecoveryHost.BuildAsync(builder, moduleManagement);
    await recoveryHost.RunAsync();
    return;
}
var startupStage = "selection";
var startupVerified = false;
try
{
    var serverModules = moduleManagement.Writable
        ? ServerModules.CreateManaged(moduleManagement.DesiredIds, installedModules)
        : ServerModules.Create(builder.Configuration, installedModules);
    builder.Services.AddSingleton(moduleManagement);
    builder.Services.AddSingleton<ModuleManagementExecutor>();
    builder.Services.AddHostedService(services => services.GetRequiredService<ModuleManagementExecutor>());
    builder.Services.AddSingleton(serverModules);
    builder.Services.AddHostedService<ModuleHealthMonitor>();
    builder.Services.AddHostedService<ModuleCatalogRefreshMonitor>();
    builder.Services.AddSingleton<CoreModulePrivilegeBroker>();
    builder.Services.AddHostedService(services => services.GetRequiredService<CoreModulePrivilegeBroker>());
    builder.Services.AddSingleton<ModuleHostSupervisor>();
    builder.Services.AddHostedService(services => services.GetRequiredService<ModuleHostSupervisor>());

    var eventAlertsOptions = builder.AddServerObservability(isDevelopment);
    // Deployment mode is an explicit security contract. In particular, Development must not turn a
    // system installation into User Mode or enable its in-process PAM path.
    // The ordinary-user execution backend is the same kind of decision, so it is resolved and
    // validated once, next to the mode, and passed along rather than re-read per call site.
    var userExecutionBackend = RelaxKonOS.Server.UserExecution.UserExecutionBackendResolver.Resolve(builder.Configuration, builder.Environment);
    var serverModeResolver = new ServerModeResolver(builder.Configuration, userExecutionBackend);
    builder.Services.AddSingleton<IServerModeResolver>(serverModeResolver);
    var kestrelCertificates = builder.ConfigureHostListeners(serverModeResolver);

    // Persisted host secrets must survive upgrades that change the content root.
    builder.Services.AddHostDataProtection();
    builder.AddProxyRuntimeServices();
    var forwardedHeaders = builder.AddServerAuthentication();
    builder.AddDeploymentServices();
    var (linuxPamTransport, privilegedHelperOptions) = builder.AddHostIdentity(serverModeResolver, userExecutionBackend);
    builder.AddHostServices(kestrelCertificates);
    // 持久化仓储：按 Storage:Provider 选择 sqlite（EF Core + SQLite，默认）或 memory（内存，开发回退）。
    // User / Workspace（身份与会话归属）/ Device 持久化；Workspace 配置由注册表持久化。
    // 详见 docs/RelaxKonOS.Storage.md。
    builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
    var storageOpts = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
    var storageProvider = string.IsNullOrWhiteSpace(storageOpts.Provider) ? "sqlite" : storageOpts.Provider.ToLowerInvariant();

    using var identityHostLock = storageProvider == "sqlite"
        ? StorageStartup.AcquireIdentityHostLock(Path.Combine(builder.Environment.ContentRootPath, storageOpts.DatabasePath)) : null;
    builder.AddServerStorage(storageOpts, storageProvider);
    builder.AddWorkspaceServices();
    // Modules register after the storage provider is resolved: a module whose persistence exists for
    // one provider only must be able to say so without duplicating the "default is sqlite" rule.
    var serverModuleContext = ServerModuleContext.Create(builder.Configuration, builder.Environment, storageProvider);
    // The module inventory route reads the same snapshot the gate uses, so both answer with one source.
    builder.Services.AddSingleton(serverModuleContext);
    startupStage = "configuration";
    serverModules.ConfigureServices(builder, serverModuleContext, moduleManagement.EffectivePolicies);

    startupStage = "build";
    var app = builder.Build();
    if (await app.RunMaintenanceAsync(builder, serverModeResolver)) return;
    app.Logger.LogInformation("Authentication configuration loaded. ServerMode={ServerMode} LinuxPamTransport={LinuxPamTransport} PrivilegedHelperPathConfigured={PrivilegedHelperPathConfigured} UserExecutionBackend={UserExecutionBackend}",
        serverModeResolver.Mode, linuxPamTransport, !string.IsNullOrWhiteSpace(builder.Configuration["PrivilegedHelper:HelperPath"]), userExecutionBackend);
    IdentityServiceRegistration.ReportUserExecutionBackend(app.Logger, userExecutionBackend, serverModeResolver.Mode, privilegedHelperOptions);
    IdentityServiceRegistration.ReportPrivilegedTransport(app.Logger, serverModeResolver, privilegedHelperOptions);
    app.Logger.LogInformation("Server modules enabled: {ServerModules}", serverModules.Describe());
    // A disabled module must not leave durable work unresolved. This runs before the request pipeline and
    // before any hosted service starts, so the operator sees the reason instead of a half-working server
    // that never reconciles an interrupted operation.
    startupStage = "deactivation";
    await serverModules.ValidateDeactivationAsync(app.Services, serverModuleContext, app.Lifetime.ApplicationStopping);
    // The sweep only decides when an idle module may be released; nothing is loaded here.
    if (app.Services.GetService<ModuleResidencyCoordinator>() is { } moduleResidency)
    {
        app.Logger.LogInformation("Server module residency: {ModuleResidency}", moduleResidency.DescribeSettings());
        moduleResidency.Start(app.Lifetime.ApplicationStopping);
    }
    if (eventAlertsOptions.Enabled)
        app.Services.GetRequiredService<RelaxKonOS.Server.Hubs.EventAlertNotificationHub>().Attach();

    app.UseServerRequestObservation(forwardedHeaders);
    startupStage = "storage";
    await app.InitializeServerStorageAsync(storageProvider);
    startupStage = "mapping";
    app.MapServerApplication(eventAlertsOptions, serverModuleContext);
    // Only acknowledge a pending set after startup validation, storage initialization and mapping.
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        moduleManagement.VerifyStartup(serverModules.EnabledIds);
        try
        {
            ModulePackageOperationStore.VerifyStartup(app.Environment.ContentRootPath,
                InstalledModuleStore.Read(app.Environment.ContentRootPath), moduleManagement.ProcessGeneration, DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            // A package-operation receipt is durable evidence, not a reason to present a healthy host
            // as failed. Leave the pending intent for the next startup and keep the failure observable.
            app.Logger.LogWarning(exception, "Module package operation verification was unavailable.");
        }
        ModulePackageStartupGuard.Complete(app.Environment.ContentRootPath);
        startupVerified = true;
    });
    startupStage = "host";
    app.Run();
}
catch
{
    if (!startupVerified)
    {
        try { moduleManagement.RecordStartupFailure(startupStage); }
        catch { Console.Error.WriteLine("module-startup-failure-record-unavailable"); }
    }
    throw;
}
