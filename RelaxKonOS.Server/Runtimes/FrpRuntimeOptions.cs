namespace RelaxKonOS.Server.Runtimes;

/// <summary>Host-admin supplied trust manifest. HTTP callers can select only a pinned Version.</summary>
public sealed class FrpRuntimeOptions
{
    public long MaximumArchiveBytes { get; init; } = 128L * 1024 * 1024;
    public IReadOnlyList<FrpRuntimeRelease> Releases { get; init; } = [];
}

public sealed class FrpRuntimeRelease
{
    public string Version { get; init; } = "";
    public string Rid { get; init; } = "";
    public string Url { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string ArchiveFormat { get; init; } = "";
}
