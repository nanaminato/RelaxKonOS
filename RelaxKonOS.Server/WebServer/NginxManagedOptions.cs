namespace RelaxKonOS.Server.WebServer;

/// <summary>
/// Installation options for the RelaxKonOS-owned Nginx instance. Linux hosts with APT use the
/// built-in, fixed package installation path when no custom installer is supplied. The API never
/// accepts an executable path, URL, package or command line from a client.
/// </summary>
public sealed class NginxManagedOptions
{
    /// <summary>Absolute RelaxKonOS-owned root. Empty selects the platform default.</summary>
    public string InstallationRoot { get; init; } = string.Empty;

}
