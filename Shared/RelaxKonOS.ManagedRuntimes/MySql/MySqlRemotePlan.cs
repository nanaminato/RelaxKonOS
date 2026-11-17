using RelaxKonOS.Protocol.Databases;

namespace RelaxKonOS.ManagedRuntimes.MySql;

public static class MySqlRemotePlan
{
    public const int Port = 3306;

    public static string FirewallTable(Guid id) => "rkmysql_" + id.ToString("N");

    public static string Firewall(MySqlRemotePolicy policy, bool allowSource, Guid? owner = null)
    {
        RequireValid(policy);
        var table = FirewallTable(policy.InstanceId);
        var chain = $"inet {table} ingress";
        var comment = owner is { } id ? $" {{ comment \"{id:N}\"; }}" : "";
        var rules = $"add table inet {table}{comment}\nflush table inet {table}\n"
            + $"add chain {chain} {{ type filter hook input priority -10; policy accept; }}\n";
        if (allowSource)
            rules += $"add rule {chain} ip saddr {policy.SourceAddress} ip daddr {policy.ListenAddress} tcp dport {Port} accept\n";
        return rules + $"add rule {chain} ip daddr {policy.ListenAddress} tcp dport {Port} counter drop\n";
    }

    public static string FirewallSnapshot(MySqlRemotePolicy policy, Guid owner, bool allowSource)
    {
        RequireValid(policy);
        var allowed = allowSource ? $"\t\tip saddr {policy.SourceAddress} ip daddr {policy.ListenAddress} tcp dport {Port} accept\n" : "";
        return $"table inet {FirewallTable(policy.InstanceId)} {{\n\tcomment \"{owner:N}\"\n\tchain ingress {{\n"
            + "\t\ttype filter hook input priority -10; policy accept;\n" + allowed
            + $"\t\tip daddr {policy.ListenAddress} tcp dport {Port} counter drop\n\t}}\n}}";
    }

    public static string Unit(MySqlRemotePolicy policy)
    {
        RequireValid(policy);
        var plan = MySqlBootstrapPolicy.Plan(policy.InstanceId);
        var root = MaterialDirectory(policy.InstanceId);
        var command = plan.UnitText.Split('\n').Single(line => line.StartsWith("ExecStart=", StringComparison.Ordinal))
            .Replace("--skip-networking", $"--bind-address={policy.ListenAddress} --port={Port}", StringComparison.Ordinal);
        return "[Service]\nExecStart=\n"
            + command + " --persisted-globals-load=OFF --skip-name-resolve --require-secure-transport=ON"
            + $" --tls-version=TLSv1.2,TLSv1.3 --ssl-cert={root}/server.pem --ssl-key={root}/server.key\n"
            + $"ExecStartPre=+/usr/sbin/nft -f /var/lib/relaxkonos/mysql-control/remote-{policy.InstanceId:N}.nft\n";
    }

    public static string MaterialDirectory(Guid id) => $"/var/lib/relaxkonos/mysql/{id:N}/remote-tls";

    private static void RequireValid(MySqlRemotePolicy policy)
    {
        if (!policy.Valid) throw new ArgumentException("database.remote_request_invalid", nameof(policy));
    }
}
