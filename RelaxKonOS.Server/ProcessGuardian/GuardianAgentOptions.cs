namespace RelaxKonOS.Server.ProcessGuardian;

/// <summary>Local Agent IPC configuration. The secret must be supplied by protected host configuration.</summary>
public sealed class GuardianAgentOptions
{
    public string PipeName { get; init; } = "relaxkonos-guardian";
    public string SharedSecret { get; init; } = string.Empty;
}
