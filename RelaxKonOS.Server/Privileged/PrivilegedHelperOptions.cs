namespace RelaxKonOS.Server.Privileged;

/// <summary>Location of the root-owned local privileged helper installed with RelaxKonOS.</summary>
public sealed class PrivilegedHelperOptions
{
    public string HelperPath { get; init; } = string.Empty;
    public string SudoPath { get; init; } = "/usr/bin/sudo";
    public int TimeoutSeconds { get; init; } = 30;
    /// <summary>Package operations may run apt update and apt install, each with a ten-minute Helper deadline.</summary>
    public int PackageOperationTimeoutSeconds { get; init; } = 1500;
    /// <summary>
    /// Linux-only persistent Helper socket. The installer sets it; leaving it empty keeps the
    /// transitional per-call sudo worker, which is now limited to development and user execution.
    /// </summary>
    public string SocketPath { get; init; } = string.Empty;
    /// <summary>Windows-only local pipe. It is never a network endpoint.</summary>
    public string PipeName { get; init; } = "relaxkonos-privileged-helper";
    /// <summary>Installation-generated machine secret, readable only by Server and Helper.</summary>
    public string SharedSecret { get; init; } = string.Empty;
}
