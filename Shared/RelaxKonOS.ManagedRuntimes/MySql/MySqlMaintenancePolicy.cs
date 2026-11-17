namespace RelaxKonOS.ManagedRuntimes.MySql;

public static class MySqlMaintenancePolicy
{
    public const string Baseline = "8.4.11";
    public const string Target = MySqlBootstrapPolicy.Version;

    // Sizes and hashes from the Oracle-signed Noble amd64 8.4.11 archive.
    private static readonly IReadOnlyList<MySqlBootstrapArtifact> BaselineArtifacts = Array.AsReadOnly<MySqlBootstrapArtifact>(
    [
        new("mysql-community-server-core", 32065392, "dafb704d3e602980ef6dcadfbd9ca8b8660736cdbfbcbd1cdd5c0638ab097cfd", "8.4.11-1ubuntu24.04"),
        new("mysql-community-client-core", 1822154, "6660b0d31fe5113997baabd48d1f4bdce66ab7e1e8446886366a3c3896424e9d", "8.4.11-1ubuntu24.04"),
        new("mysql-community-client-plugins", 1451722, "1b48ea921a15622c2bf2756dded1f2c82e074458f8bcc00f5d27f857ce882bf4", "8.4.11-1ubuntu24.04")
    ]);

    public static IReadOnlyList<MySqlBootstrapArtifact>? Artifacts(string version) => version switch
    {
        Baseline => BaselineArtifacts,
        Target => MySqlBootstrapPolicy.Artifacts,
        _ => null
    };

    public static bool CanUpdate(string source, string target) => source == Baseline && target == Target;
}
