using RoyalTerminal.Terminal;

namespace RelaxKonOS.Server.Terminal;

/// <summary>
/// Selects in-process PTYs for User Mode and identity-verified Helper PTYs for System Mode.
/// </summary>
public sealed class PlatformPtyFactory(IServiceScopeFactory scopes, IHttpContextAccessor http, RelaxKonOS.Server.HostMode.IServerModeResolver mode,
    RelaxKonOS.Server.UserExecution.UserExecutionBackendSelection userExecution, RelaxKonOS.Server.Privileged.PrivilegedHelperOptions helper) : IPtyFactory
{
    private readonly IPtyFactory _fallback = new DefaultPtyFactory();

    public IPty Create()
    {
        // In-process whenever the process already is the effective user: User Mode has always worked
        // that way, and the local-identity backend is only permitted while that equality holds.
        if (mode.Mode != RelaxKonOS.Protocol.Common.ServerMode.System || userExecution.Backend == RelaxKonOS.Server.UserExecution.UserExecutionBackend.LocalIdentity)
            return OperatingSystem.IsWindows() ? new ConPty() : _fallback.Create();
        if (userExecution.Backend != RelaxKonOS.Server.UserExecution.UserExecutionBackend.Helper)
            throw new InvalidOperationException("User terminal execution is disabled.");
        using var scope = scopes.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<RelaxKonOS.Server.UserExecution.IUserExecutionContextResolver>();
        var principal = http.HttpContext?.User ?? throw new InvalidOperationException("Terminal requires an authenticated user.");
        var context = resolver.Resolve(principal);
        if (OperatingSystem.IsWindows())
            return new WindowsUserTerminalPty(context,
                scope.ServiceProvider.GetRequiredService<RelaxKonOS.Server.UserExecution.WindowsNamedPipeUserExecutionTransport>());
        if (OperatingSystem.IsLinux()) return new LinuxUserTerminalPty(context, helper);
        throw new PlatformNotSupportedException("System Mode user terminals are not implemented on this platform.");
    }
}
