using RoyalTerminal.Terminal;

namespace RelaxKonOS.Server.Terminal;

/// <summary>
/// PTY factory that uses the corrected ConPTY implementation on Windows and the
/// package's forkpty-based implementation (which is not affected by the Windows
/// CreatePseudoConsole P/Invoke bug) on Unix.
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
        // System Mode without that equality needs a terminal owned by another account, which only the
        // Linux Helper implements today: it drops to the target UID before creating the PTY.
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("System Mode user terminals are not implemented on this platform.");
        using var scope = scopes.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<RelaxKonOS.Server.UserExecution.IUserExecutionContextResolver>();
        var principal = http.HttpContext?.User ?? throw new InvalidOperationException("Terminal requires an authenticated user.");
        return new LinuxUserTerminalPty(resolver.Resolve(principal), helper);
    }
}
