namespace RelaxKonOS.AppSDK;

/// <summary>
/// Implemented by an application that can create a terminal whose initial working directory is
/// a remote-host folder. The runtime uses this contract to let other built-in applications open
/// a terminal without depending on a particular terminal implementation.
/// </summary>
public interface IOpenTerminalApplication
{
    /// <summary>Opens a new terminal with <paramref name="workingDirectory"/> as its initial directory.</summary>
    void OpenTerminal(AppContext context, string workingDirectory);
}
