namespace RelaxKonOS.Server.Docker;

/// <summary>Host-owned location for persisted Compose sources.</summary>
public sealed class DockerComposeOptions
{
    /// <summary>
    /// Absolute directory for deployed Compose files. Leave empty to use the
    /// platform default; relative paths are deliberately rejected so runtime data
    /// can never silently fall back into the application/source directory.
    /// </summary>
    public string? DataDirectory { get; set; }
}
