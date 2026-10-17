using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Secrets;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Storage;

if (args.FirstOrDefault() == "auth") { Environment.ExitCode = await AuthMaintenanceCommand.RunAsync(args); return; }
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
builder.AddProxyServices();
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

var app = builder.Build();
if (await app.RunMaintenanceAsync(builder, serverModeResolver)) return;
app.Logger.LogInformation("Authentication configuration loaded. ServerMode={ServerMode} LinuxPamTransport={LinuxPamTransport} PrivilegedHelperPathConfigured={PrivilegedHelperPathConfigured} UserExecutionBackend={UserExecutionBackend}",
    serverModeResolver.Mode, linuxPamTransport, !string.IsNullOrWhiteSpace(builder.Configuration["PrivilegedHelper:HelperPath"]), userExecutionBackend);
IdentityServiceRegistration.ReportUserExecutionBackend(app.Logger, userExecutionBackend, serverModeResolver.Mode, privilegedHelperOptions);
if (eventAlertsOptions.Enabled)
    app.Services.GetRequiredService<RelaxKonOS.Server.Hubs.EventAlertNotificationHub>().Attach();

app.UseServerRequestObservation(forwardedHeaders);
await app.InitializeServerStorageAsync(storageProvider);
app.MapServerApplication(eventAlertsOptions);
app.Run();
