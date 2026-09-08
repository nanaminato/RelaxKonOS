namespace RelaxKonOS.Server.Privileged;

/// <summary>Location of the root-owned local privileged helper installed with RelaxKonOS.</summary>
public sealed class PrivilegedHelperOptions
{
    public string HelperPath { get; init; } = string.Empty;
    public string SudoPath { get; init; } = "/usr/bin/sudo";
    public int TimeoutSeconds { get; init; } = 30;
    /// <summary>Windows-only local pipe. It is never a network endpoint.</summary>
    public string PipeName { get; init; } = "relaxkonos-privileged-helper";
    /// <summary>Installation-generated machine secret, readable only by Server and Helper.</summary>
    public string SharedSecret { get; init; } = string.Empty;
}
