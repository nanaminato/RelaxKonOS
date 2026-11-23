using RelaxKonOS.Client.Services.Developer;

namespace RelaxKonOS.Client.Services;

/// <summary>
/// Single root for client-owned persistent files. Resolve only after startup has selected and
/// verified the data directory. Temporary transfers, explicit user saves and OS keyrings have
/// separate ownership; the executable-side location record is needed to discover this root.
/// </summary>
public static class ClientDataPaths
{
    public static string Root => DeveloperProfile.DataDirectory;
}
