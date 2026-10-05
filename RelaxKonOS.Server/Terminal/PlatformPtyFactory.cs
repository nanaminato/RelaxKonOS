using RoyalTerminal.Terminal;

namespace RelaxKonOS.Server.Terminal;

/// <summary>
/// Selects in-process owner PTYs and identity-verified Helper PTYs, including personal administrator terminals.
/// </summary>
public sealed class PlatformPtyFactory(IServiceScopeFactory scopes, IHttpContextAccessor http, RelaxKonOS.Server.HostMode.IServerModeResolver mode,
    RelaxKonOS.Server.UserExecution.UserExecutionBackendSelection userExecution, RelaxKonOS.Server.Privileged.PrivilegedHelperOptions helper) : IPtyFactory
{
    private readonly IPtyFactory _fallback = new DefaultPtyFactory();

    public IPty CreateAdministrator()
    {
        if (mode.Mode != RelaxKonOS.Protocol.Common.ServerMode.User || !OperatingSystem.IsWindows()) return Create();
        using var scope = scopes.CreateScope();
        var principal = http.HttpContext?.User ?? throw new InvalidOperationException("Terminal requires an authenticated user.");
        var context = scope.ServiceProvider.GetRequiredService<RelaxKonOS.Server.UserExecution.IUserExecutionContextResolver>().Resolve(principal);
        return new WindowsUserTerminalPty(context,
            scope.ServiceProvider.GetRequiredService<RelaxKonOS.Server.UserExecution.WindowsNamedPipeUserExecutionTransport>());
    }

    public IPty Create()
    {
        using var scope = scopes.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<RelaxKonOS.Server.UserExecution.IUserExecutionContextResolver>();
        var principal = http.HttpContext?.User ?? throw new InvalidOperationException("Terminal requires an authenticated user.");
        var context = resolver.Resolve(principal);
        // In-process whenever the process already is the effective user: User Mode has always worked
        // that way, and the local-identity backend is only permitted while that equality holds.
        if (mode.Mode != RelaxKonOS.Protocol.Common.ServerMode.System || userExecution.Backend == RelaxKonOS.Server.UserExecution.UserExecutionBackend.LocalIdentity)
            return new LocalUserTerminalPty(OperatingSystem.IsWindows() ? new ConPty() : _fallback.Create(), context);
        if (userExecution.Backend != RelaxKonOS.Server.UserExecution.UserExecutionBackend.Helper)
            throw new InvalidOperationException("User terminal execution is disabled.");
        if (OperatingSystem.IsWindows())
            return new WindowsUserTerminalPty(context,
                scope.ServiceProvider.GetRequiredService<RelaxKonOS.Server.UserExecution.WindowsNamedPipeUserExecutionTransport>());
        if (OperatingSystem.IsLinux()) return new LinuxUserTerminalPty(context, helper);
        throw new PlatformNotSupportedException("System Mode user terminals are not implemented on this platform.");
    }
}
