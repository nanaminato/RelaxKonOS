namespace RelaxKonOS.Server.Terminal;

/// <summary>Confirms this backend's owned reader/process cleanup; cancellation may stop waiting, never detach cleanup.</summary>
public interface ITerminalPtyLifetime
{
    Task StopAsync();
}
