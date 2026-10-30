namespace RelaxKonOS.Server.Runtimes;

/// <summary>Host-admin supplied trust manifest. HTTP callers can select only a pinned Version.</summary>
public sealed class FrpRuntimeOptions
{
    public void BindConfiguration(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        // The binder appends array entries to initialized collections unless they are cleared first.
        if (configuration.GetSection("Releases").Exists()) Releases = [];
        configuration.Bind(this);
    }

    public long MaximumArchiveBytes { get; init; } = 128L * 1024 * 1024;
    public IReadOnlyList<FrpRuntimeRelease> Releases { get; set; } =
        RelaxKonOS.Protocol.Privileged.WindowsManagedRuntimeDefaults.FrpReleases.Concat([
            RelaxKonOS.ManagedRuntimes.ComponentPackages.PinnedArchives.LinuxFrp(false),
            RelaxKonOS.ManagedRuntimes.ComponentPackages.PinnedArchives.LinuxFrp(true)
        ]).Select(pin => new FrpRuntimeRelease { Version = pin.Version, Rid = pin.Rid, Url = pin.Url,
            Sha256 = pin.Sha256, ArchiveFormat = pin.ArchiveFormat }).ToArray();
}

public sealed class FrpRuntimeRelease
{
    public string Version { get; init; } = "";
    public string Rid { get; init; } = "";
    public string Url { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string ArchiveFormat { get; init; } = "";
}
