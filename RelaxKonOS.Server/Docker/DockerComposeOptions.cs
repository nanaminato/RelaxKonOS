namespace RelaxKonOS.Server.Docker;

/// <summary>Host-owned location and bounds for the persisted Compose domain.</summary>
public sealed class DockerComposeOptions
{
    /// <summary>
    /// Absolute directory for deployed Compose files and the stack operation ledger. Leave empty to use
    /// the platform default; relative paths are deliberately rejected so runtime data can never silently
    /// fall back into the application/source directory.
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary>How many stack operations may run at once. Projects are mutually exclusive regardless of
    /// this value; the ceiling only stops a burst of separate projects from saturating the host.</summary>
    public int MaximumConcurrentOperations { get; set; } = 2;
}
