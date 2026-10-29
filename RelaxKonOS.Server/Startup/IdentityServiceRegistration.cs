using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Identity;

namespace RelaxKonOS.Server.Startup;

internal static class IdentityServiceRegistration
{
    public static (string LinuxPamTransport, RelaxKonOS.Server.Privileged.PrivilegedHelperOptions HelperOptions) AddHostIdentity(this WebApplicationBuilder builder, ServerModeResolver serverModeResolver, RelaxKonOS.Server.UserExecution.UserExecutionBackend userExecutionBackend)
    {
        // Identity transport is a deployment decision, not a Development-environment shortcut.  A
        // no-sudo User Mode Server authenticates the account that owns the process through the host's
        // already configured PAM "login" service; a managed System Mode Server delegates the same
        // operation to its root-owned, closed Helper.  Debug builds may use either transport, but the
        // environment name must never silently change authentication or privilege behavior.
        var linuxPamTransport = builder.Configuration["Identity:LinuxPamTransport"]?.Trim().ToLowerInvariant() ?? "helper";
        if (linuxPamTransport is not ("helper" or "in-process"))
            throw new InvalidOperationException("Identity:LinuxPamTransport must be 'helper' or 'in-process'.");
        var useInProcessLinuxPam = linuxPamTransport == "in-process";
        var linuxPamService = builder.Configuration["Identity:LinuxPamService"]?.Trim() ?? "login";
        if (OperatingSystem.IsLinux() && useInProcessLinuxPam && !LinuxPamProvider.IsValidPamServiceName(linuxPamService))
            throw new InvalidOperationException("Identity:LinuxPamService is invalid.");
        if (OperatingSystem.IsWindows())
            builder.Services.AddSingleton<IIdentityProvider>(_ => OperatingSystem.IsWindows()
                ? new BoundedIdentityProvider(new WindowsLogonProvider())
                : throw new PlatformNotSupportedException("Windows identity provider requires Windows."));
        else if (OperatingSystem.IsLinux())
            builder.Services.AddSingleton<IIdentityProvider>(sp => OperatingSystem.IsLinux() ? new BoundedIdentityProvider(new LinuxPamProvider(
                useInProcessLinuxPam ? null : sp.GetRequiredService<RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport>(),
                useInProcessLinuxPam, linuxPamService)) : throw new PlatformNotSupportedException("Linux identity provider requires Linux."));
        else
            throw new PlatformNotSupportedException("RelaxKonOS Server identity authentication supports Windows and Linux hosts only.");

        // The Server normally remains an unprivileged service. The optional helper is a root-owned
        // local executable invoked only after FileEndpoints has granted a short-lived file elevation.
        var privilegedHelperOptions = builder.Configuration.GetSection("PrivilegedHelper").Get<RelaxKonOS.Server.Privileged.PrivilegedHelperOptions>()
                                     ?? new RelaxKonOS.Server.Privileged.PrivilegedHelperOptions();
        builder.Services.AddSingleton(privilegedHelperOptions);
        // The terminal factory and the Git domain both need the same deployment decision, so the resolved
        // value is published once instead of each of them re-reading configuration.
        builder.Services.AddSingleton(new RelaxKonOS.Server.UserExecution.UserExecutionBackendSelection(userExecutionBackend));
        builder.Services.AddSingleton<RelaxKonOS.Server.Privileged.LocalPrivilegedOperationRunner>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Privileged.UnixSocketPrivilegedOperationTransport>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport>(sp =>
            !serverModeResolver.Supports(ServerHostFeature.PrivilegedOperations)
                ? new RelaxKonOS.Server.Privileged.DisabledPrivilegedOperationTransport()
                : OperatingSystem.IsWindows()
                ? ActivatorUtilities.CreateInstance<RelaxKonOS.Server.Privileged.WindowsNamedPipePrivilegedOperationTransport>(sp)
                // A configured socket is the production Linux path. Without one (a development
                // profile that has not installed the unit yet) the transitional per-call sudo
                // worker remains explicit configuration, not a silent fallback from the socket.
                : !string.IsNullOrWhiteSpace(privilegedHelperOptions.SocketPath)
                    ? sp.GetRequiredService<RelaxKonOS.Server.Privileged.UnixSocketPrivilegedOperationTransport>()
                    : sp.GetRequiredService<RelaxKonOS.Server.Privileged.LocalPrivilegedOperationRunner>());
        builder.Services.AddSingleton<RelaxKonOS.Server.UserExecution.LinuxUserExecutionTransport>();
        builder.Services.AddSingleton<RelaxKonOS.Server.UserExecution.WindowsNamedPipeUserExecutionTransport>();
        builder.Services.AddSingleton<RelaxKonOS.Server.UserExecution.LocalIdentityUserExecutionTransport>();
        // User Mode keeps the semantics it has always had: the process is the effective user, so it
        // executes in-process and never crosses a Helper boundary. The backend selector therefore only
        // applies to System Mode, where the effective user is somebody other than the Server account.
        builder.Services.AddSingleton<RelaxKonOS.Server.UserExecution.IUserExecutionTransport>(sp =>
            serverModeResolver.Mode == RelaxKonOS.Protocol.Common.ServerMode.User
                ? new RelaxKonOS.Server.UserExecution.DisabledUserExecutionTransport()
                : userExecutionBackend switch
                {
                    RelaxKonOS.Server.UserExecution.UserExecutionBackend.LocalIdentity =>
                        sp.GetRequiredService<RelaxKonOS.Server.UserExecution.LocalIdentityUserExecutionTransport>(),
                    RelaxKonOS.Server.UserExecution.UserExecutionBackend.Disabled =>
                        new RelaxKonOS.Server.UserExecution.DisabledUserExecutionTransport(),
                    _ => OperatingSystem.IsLinux()
                        ? sp.GetRequiredService<RelaxKonOS.Server.UserExecution.LinuxUserExecutionTransport>()
                        : OperatingSystem.IsWindows()
                            ? sp.GetRequiredService<RelaxKonOS.Server.UserExecution.WindowsNamedPipeUserExecutionTransport>()
                            : new RelaxKonOS.Server.UserExecution.DisabledUserExecutionTransport(),
                });
        builder.Services.AddSingleton<RelaxKonOS.Server.Privileged.IPrivilegedFileService, RelaxKonOS.Server.Privileged.PrivilegedFileService>();
        builder.Services.AddSingleton(sp => new RelaxKonOS.Server.Privileged.HostElevationSessionState(
            sp.GetRequiredService<RelaxKonOS.Server.Settings.ServerRuntimeSettingsStore>(),
            sp.GetRequiredService<RelaxKonOS.Server.Identity.SessionValidityService>()));
        builder.Services.AddScoped<RelaxKonOS.Server.Privileged.IHostElevationSessionStore, RelaxKonOS.Server.Privileged.HostElevationSessionStore>();
        builder.Services.AddScoped<RelaxKonOS.Server.Privileged.IFileElevationSessionStore, RelaxKonOS.Server.Privileged.FileElevationSessionStore>();
        builder.Services.AddScoped<RelaxKonOS.Server.Privileged.IHostAdministratorAuthenticator, RelaxKonOS.Server.Privileged.HostAdministratorAuthenticator>();
        builder.Services.AddScoped<RelaxKonOS.Server.Privileged.IHostAccountPrivilegeService, RelaxKonOS.Server.Privileged.HostAccountPrivilegeService>();
        builder.Services.AddScoped<RelaxKonOS.Server.Privileged.IHostFileAuthorizationService, RelaxKonOS.Server.Privileged.HostFileAuthorizationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ProcessGuardian.IPrivilegedNativeServiceOperations, RelaxKonOS.Server.ProcessGuardian.PrivilegedNativeServiceOperations>();
        builder.Services.AddSingleton<RelaxKonOS.Server.WebServer.IPrivilegedNginxOperations, RelaxKonOS.Server.WebServer.PrivilegedNginxOperations>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Privileged.ManagedRuntimeOperations>();
        builder.Services.AddSingleton<RelaxKonOS.Server.FileServices.IPrivilegedSmbOperations, RelaxKonOS.Server.FileServices.PrivilegedSmbOperations>();
        builder.Services.AddSingleton<RelaxKonOS.Server.FileServices.ISambaPlatformAdapter, RelaxKonOS.Server.FileServices.LinuxSambaPlatformAdapter>();
        builder.Services.AddSingleton<RelaxKonOS.Server.FileServices.IWindowsSmbPlatformAdapter, RelaxKonOS.Server.FileServices.WindowsSmbPlatformAdapter>();
        builder.Services.AddSingleton<RelaxKonOS.Server.FileServices.IWindowsSmbOwnershipLedger, RelaxKonOS.Server.FileServices.WindowsSmbOwnershipLedger>();
        builder.Services.AddSingleton<RelaxKonOS.Server.FileServices.IFileServiceAudit, RelaxKonOS.Server.FileServices.FileServiceAudit>();
        builder.Services.AddSingleton<RelaxKonOS.Server.FileServices.IFileServiceProvider, RelaxKonOS.Server.FileServices.LinuxSambaFileServiceProvider>();
        builder.Services.AddSingleton<RelaxKonOS.Server.FileServices.IFileServiceProvider, RelaxKonOS.Server.FileServices.WindowsSmbFileServiceProvider>();
        builder.Services.AddSingleton<RelaxKonOS.Server.FileServices.IFileServiceProviderResolver, RelaxKonOS.Server.FileServices.FileServiceProviderResolver>();
        builder.Services.AddSingleton<RelaxKonOS.Server.FileServices.IFileServiceManager, RelaxKonOS.Server.FileServices.FileServiceManager>();
        return (linuxPamTransport, privilegedHelperOptions);
    }

    /// <summary>
    /// Says which process performs ordinary user operations, and — when that is a Helper that is not
    /// reachable — names both ways out. Without it the only symptom is a bare "user-execution Helper is
    /// unavailable" on every file listing, which reads like a wrong secret even when the real cause is
    /// that the same request would have worked under another backend.
    /// </summary>
    public static void ReportUserExecutionBackend(ILogger logger, RelaxKonOS.Server.UserExecution.UserExecutionBackend backend,
        RelaxKonOS.Protocol.Common.ServerMode mode, RelaxKonOS.Server.Privileged.PrivilegedHelperOptions options)
    {
        if (mode == RelaxKonOS.Protocol.Common.ServerMode.User)
        {
            logger.LogInformation("Ordinary user operations execute in-process as the User Mode account.");
            return;
        }
        switch (backend)
        {
            case RelaxKonOS.Server.UserExecution.UserExecutionBackend.LocalIdentity:
                logger.LogWarning("Ordinary user operations execute in-process and are limited to the Server's own OS "
                    + "identity ({Identity}); any other authenticated account is refused. This backend is for local "
                    + "debugging and must not be enabled in Production.", RelaxKonOS.Server.UserExecution.ServerProcessIdentity.Describe());
                return;
            case RelaxKonOS.Server.UserExecution.UserExecutionBackend.Disabled:
                logger.LogWarning("Ordinary user operations are disabled: every file, Git and terminal request that needs "
                    + "the effective user fails closed instead of running as the Server account.");
                return;
        }
        if (OperatingSystem.IsWindows() && !NamedPipeExists(RelaxKonOS.Protocol.UserExecution.UserExecutionProtocol.WindowsPipeName(options.PipeName)))
            logger.LogWarning("The user-execution Helper pipe '{PipeName}' is not listening, so file and terminal requests "
                + "will fail with 'user-execution Helper is unavailable'. Start the Helper (deployment service, or "
                + "'--console --config <dev config>'), or set PrivilegedHelper:UserExecutionBackend=local-identity to "
                + "execute in-process as this account while debugging.",
                RelaxKonOS.Protocol.UserExecution.UserExecutionProtocol.WindowsPipeName(options.PipeName));
        else if (OperatingSystem.IsLinux() && (string.IsNullOrWhiteSpace(options.HelperPath) || !File.Exists(options.HelperPath)))
            logger.LogWarning("PrivilegedHelper:HelperPath does not name an installed Helper (HelperPathConfigured={HelperPathConfigured}), "
                + "so file and terminal requests will fail with 'user-execution Helper is unavailable'. Install the "
                + "development Helper (deployment/linux/install-relaxkonos-privileged-helper-development.sh), or set "
                + "PrivilegedHelper:UserExecutionBackend=local-identity to execute in-process as this account while debugging.",
                !string.IsNullOrWhiteSpace(options.HelperPath));
    }

    /// <summary>
    /// Names the selected privileged transport once. The socket path is installation metadata, so it
    /// is logged only as configured/missing; a missing socket is degraded, not a startup failure.
    /// </summary>
    public static void ReportPrivilegedTransport(ILogger logger, RelaxKonOS.Server.HostMode.ServerModeResolver serverModeResolver,
        RelaxKonOS.Server.Privileged.PrivilegedHelperOptions options)
    {
        if (!serverModeResolver.Supports(ServerHostFeature.PrivilegedOperations))
        {
            logger.LogInformation("Privileged operations are disabled for this Server mode.");
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            logger.LogInformation("Privileged operations use the local named-pipe Helper.");
            return;
        }
        if (string.IsNullOrWhiteSpace(options.SocketPath))
        {
            logger.LogWarning("PrivilegedHelper:SocketPath is not configured, so privileged operations use the transitional "
                + "per-call sudo worker. Install the persistent Helper service for production.");
            return;
        }
        if (!Path.Exists(options.SocketPath))
            logger.LogWarning("The privileged Helper socket is not present yet (SocketPathConfigured=True), so privileged "
                + "operations will fail with 'privileged helper service is unavailable' until relaxkonos-privileged-helper.service starts.");
        else
            logger.LogInformation("Privileged operations use the persistent Helper socket.");
    }

    static bool NamedPipeExists(string pipeName)
    {
        try
        {
            return Directory.GetFiles(@"\\.\pipe\")
                .Any(path => string.Equals(Path.GetFileName(path), pipeName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // A probe failure is not a reason to fail startup; the request path still fails closed.
            return true;
        }
    }
}
