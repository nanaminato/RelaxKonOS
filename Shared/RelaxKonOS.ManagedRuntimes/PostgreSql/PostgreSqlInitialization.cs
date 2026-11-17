using System.Globalization;

namespace RelaxKonOS.ManagedRuntimes.PostgreSql;

/// <summary>Fixed native inputs for a fresh, privately owned cluster. No passwords or arbitrary paths.</summary>
public sealed record PostgreSqlInitialization(
    Guid InstanceId, int Port, int Major, string ClusterName, string ServiceName,
    string DataDirectory, string ConfigurationDirectory, string SocketDirectory,
    IReadOnlyList<string> CreateArguments, string HbaConfiguration);

public static class PostgreSqlInitializationPlanner
{
    public const string ControlRoot = "/var/lib/relaxkonos/postgresql-control";

    public static string BackupRoot(Guid instanceId)
    {
        if (instanceId == Guid.Empty) throw new ArgumentException("database.instance_invalid", nameof(instanceId));
        return $"{ControlRoot}/backups/{instanceId:N}";
    }

    public static string? CheckClusterPort(string inventory, int requestedPort)
    {
        foreach (var line in inventory.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 4 || !int.TryParse(columns[2], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || port is < 1 or > 65535)
                return "database.inventory_unknown";
            if (port == requestedPort)
                return "database.port_conflict";
        }
        return null;
    }

    /// <summary>
    /// Compiles inputs only; does not create directories or launch programs. The executor must prove
    /// all paths absent, suppress package-created default clusters in its durable transaction,
    /// create a protected socket directory and validate the complete native configuration before start.
    /// </summary>
    public static PostgreSqlInitialization Create(Guid instanceId, int port, int major)
    {
        if (instanceId == Guid.Empty) throw new ArgumentException("database.instance_invalid", nameof(instanceId));
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "database.port_invalid");
        if (major is not (16 or 17 or 18)) throw new ArgumentOutOfRangeException(nameof(major));
        var name = "rk" + instanceId.ToString("N");
        var data = $"/var/lib/relaxkonos/databases/{instanceId:N}/data";
        var config = $"/etc/postgresql/{major}/{name}";
        var socket = $"/run/postgresql/{name}";
        string[] args = ["--user=postgres", "--group=postgres", "--datadir=" + data,
            "--socketdir=" + socket, "--port=" + port.ToString(CultureInfo.InvariantCulture),
            "--locale=C.UTF-8", "--start-conf=manual", "--pgoption=listen_addresses=127.0.0.1,::1",
            "--pgoption=password_encryption=scram-sha-256", major.ToString(CultureInfo.InvariantCulture), name, "--",
            "--auth-local=peer", "--auth-host=scram-sha-256", "--data-checksums"];
        // Replace the generated HBA before first start. No replication or non-loopback entries.
        const string hba = "local all postgres peer\n"
            + "local all all reject\n"
            + "host all all 127.0.0.1/32 scram-sha-256\n"
            + "host all all ::1/128 scram-sha-256\n"
            + "host all all 0.0.0.0/0 reject\n"
            + "host all all ::/0 reject\n";
        return new(instanceId, port, major, name, $"postgresql@{major}-{name}.service", data, config, socket,
            Array.AsReadOnly(args), hba);
    }
}
