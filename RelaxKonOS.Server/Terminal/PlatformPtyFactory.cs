using RoyalTerminal.Terminal;

namespace RelaxKonOS.Server.Terminal;

/// <summary>
/// PTY factory that uses the corrected ConPTY implementation on Windows and the
/// package's forkpty-based implementation (which is not affected by the Windows
/// CreatePseudoConsole P/Invoke bug) on Unix.
/// </summary>
public sealed class PlatformPtyFactory : IPtyFactory
{
    private readonly IPtyFactory _fallback = new DefaultPtyFactory();

    public IPty Create() =>
        OperatingSystem.IsWindows() ? new ConPty() : _fallback.Create();
}