using RelaxKonOS.ManagedRuntimes.PostgreSql;
public static partial class PrivilegedOperationExecutor
{
    private static int PgMajor(PgLedger ledger) => ledger.Package?.PackageName switch
    {
        "postgresql-16" => 16,
        "postgresql-18" => 18,
        _ => throw new PgFailure("database.version_unsupported")
    };
    private static string PgBin(PgLedger ledger, string tool) => PgBin(PgMajor(ledger), tool);
    private static string PgBin(int major, string tool)
    {
        if (major is not (16 or 18) || tool is not ("postgres" or "psql" or "pg_ctl" or "pg_checksums" or "pg_upgrade"))
            throw new PgFailure("database.version_unsupported");
        return $"/usr/lib/postgresql/{major}/bin/{tool}";
    }
    private static int PgDataMajor(string data)
    {
        PgNoLinks(data + "/PG_VERSION");
        return File.ReadAllText(data + "/PG_VERSION").Trim() switch
        { "16" => 16, "18" => 18, _ => throw new PgFailure("database.instance_invalid") };
    }
}
