using Microsoft.AspNetCore.SignalR;
using RoyalTerminal.Terminal;
using RelaxKonOS.Server.Hubs;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Observability;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Startup;

internal static class WorkspaceServiceRegistration
{
    public static void AddWorkspaceServices(this WebApplicationBuilder builder)
    {
        // 终端：服务端 PTY 工厂（Windows ConPTY / Unix forkpty）+ 持久会话管理器 + SignalR Hub。
        // AddSignalR 由 Microsoft.NET.Sdk.Web 隐式 FrameworkReference 提供，无需额外 NuGet。
        builder.Services.AddSingleton<IPtyFactory, RelaxKonOS.Server.Terminal.PlatformPtyFactory>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Terminal.TerminalSessionManager>();
        // 以 JWT sub claim 作为 Hub UserIdentifier，供 TerminalHub 按用户索引/过滤持久会话。
        builder.Services.AddSingleton<IUserIdProvider, RelaxKonOS.Server.Terminal.TerminalUserIdProvider>();
        builder.Services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = null;
            options.AddFilter<SessionValidityHubFilter>();
            options.AddFilter<ObservationHubFilter>();
        });
        builder.Services.AddSingleton<GuardianLogSubscriptionRegistry>();
        builder.Services.AddHostedService<GuardianLogBroadcastService>();
        builder.Services.AddHostedService<PerformanceBroadcastService>();
        builder.Services.AddSingleton(sp => new RelaxKonOS.Server.Settings.ServerRuntimeSettingsStore(Path.Combine(builder.Environment.ContentRootPath, "data", "server-runtime-settings.json")));
        builder.Services.AddSingleton<SettingsSubscriptions>();
        builder.Services.AddHostedService<SettingsChangesBroadcastService>();

        // 文件管理：以宿主 OS 进程身份执行 IO，复用宿主用户/权限（不另建 ACL——见 project_memory 硬约束）。
        // LocalFileService 移植自 Jaya FileSystemService 的目录枚举逻辑并扩展为完整文件操作；平台感知（Windows 盘符 / Linux "/" 根）。
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<RelaxKonOS.Server.Files.LocalFileService>();
        builder.Services.AddScoped<RelaxKonOS.Server.Files.IFileService, RelaxKonOS.Server.UserExecution.UserExecutionFileService>();
        // This is intentionally not a fallback to the Server account.  In this first delivery it
        // validates the closed User Mode channel and makes System Mode fail closed until the dedicated
        // user-execution Helper is installed.
        builder.Services.AddSingleton<RelaxKonOS.Server.UserExecution.IUserExecutionService,
            RelaxKonOS.Server.UserExecution.DirectUserExecutionService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Files.FileOperationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Files.MediaLeaseStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Files.MediaLeaseFileReader>();
        // 大文件分块上传：会话索引是唯一可重启恢复的偏移来源，扫描器只从索引出发（绝不全盘扫描暂存文件）。
        builder.Services.AddSingleton(sp =>
            sp.GetRequiredService<IConfiguration>().GetSection("FileUploads")
                .Get<RelaxKonOS.Server.Files.UploadSessionOptions>()
            ?? new RelaxKonOS.Server.Files.UploadSessionOptions());
        builder.Services.AddSingleton<RelaxKonOS.Server.Files.UploadSessionStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Files.UploadSessionConcurrency>();
        builder.Services.AddScoped<RelaxKonOS.Server.Files.UploadSessionService>();
        builder.Services.AddHostedService<RelaxKonOS.Server.Files.UploadSessionSweeper>();
        builder.Services.AddSingleton<WorkspaceWallpaperStore>();
        // The service only depends on the singleton runtime registry, so the singleton lifetime lets
        // background notification delivery read the cache without creating a database scope.
        builder.Services.AddSingleton<RelaxKonOS.Server.Settings.IWorkspaceSettingsService, RelaxKonOS.Server.Settings.WorkspaceSettingsService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Settings.WorkspaceEnvironmentService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Settings.SettingsOperationJournal>();
        builder.Services.AddScoped<RelaxKonOS.Server.Settings.SettingsCatalog>();
        builder.Services.AddScoped<RelaxKonOS.Server.Settings.EnvironmentOperationCoordinator>();
        builder.Services.AddScoped<RelaxKonOS.Server.Settings.IHostEnvironmentService, RelaxKonOS.Server.Settings.HostEnvironmentService>();
        builder.Services.AddScoped<RelaxKonOS.Server.Settings.IHostTimeService, RelaxKonOS.Server.Settings.HostTimeService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Settings.HostNetworkOperationStore>();
        builder.Services.AddScoped<RelaxKonOS.Server.Settings.HostNetworkService>();
        builder.Services.AddScoped<RelaxKonOS.Server.Settings.IHostIdentityService, RelaxKonOS.Server.Settings.HostIdentityService>();
        builder.Services.AddScoped<RelaxKonOS.Server.Settings.HostIdentityOperationCoordinator>();
        builder.Services.AddScoped<RelaxKonOS.Server.Settings.SettingsOperationCoordinator>();

        // CORS（开发期允许客户端跨域）
        builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

        builder.Services.AddOpenApi();
    }
}
