using RelaxKonOS.Server.Terminal;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.ServerModule.Terminal.Execution;

public sealed class LocalTerminalPtyFactory : IPtyFactory
{
    private readonly IPtyFactory _platform = new DefaultPtyFactory();

    public IPty Create() => OperatingSystem.IsWindows() ? new ConPty() : _platform.Create();
}
