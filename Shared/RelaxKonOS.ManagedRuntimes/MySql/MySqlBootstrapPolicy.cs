namespace RelaxKonOS.ManagedRuntimes.MySql;

public sealed record MySqlBootstrapArtifact(string Name, long Bytes, string Sha256, string PackageVersion = MySqlBootstrapPolicy.PackageVersion)
{
    public string FileName => $"{Name}_{PackageVersion}_amd64.deb";
    public string Uri => $"{MySqlBootstrapPolicy.Repository}/pool/mysql-8.4-lts/m/mysql-community/{FileName}";
}

public sealed record MySqlBootstrapInstance(
    Guid Id, string DataDirectory, string SocketDirectory, string ServiceName,
    IReadOnlyList<string> InitializeArguments, string UnitText);

/// <summary>Fixed first-install inputs for the Oracle Noble amd64 core package slice.</summary>
public static class MySqlBootstrapPolicy
{
    public const string Repository = "https://repo.mysql.com/apt/ubuntu";
    public const string PackageVersion = "8.4.13-1ubuntu24.04";
    public const string Version = "8.4.13";
    public const string SigningFingerprint = "BCA43417C3B485DD128EC6D4B7B3B788A8D3785C";
    public const string Server = "/usr/sbin/mysqld";

    public static IReadOnlyList<MySqlBootstrapArtifact> Artifacts { get; } = Array.AsReadOnly<MySqlBootstrapArtifact>(
    [
        new("mysql-community-server-core", 32029274, "66269aaf941bee02390df98c7b69ee01eb918298a18f10882202d113aa0f1e62"),
        new("mysql-community-client-core", 1824254, "2bcb554edd33141ac5d54579fd8ab9dc5dbeca255a0a5e29cca7ece9afa7d21e"),
        new("mysql-community-client-plugins", 1452468, "6812f8ab63482a3065da19a7cf807f0999073476ed6131561799e6ba9ec8bcc7")
    ]);

    public static bool SupportsHost(string distribution, string release, string architecture, bool systemd) =>
        distribution == "ubuntu" && release == "24.04" && architecture == "amd64" && systemd;

    public static MySqlBootstrapInstance Plan(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("database.instance_invalid", nameof(id));
        var data = $"/var/lib/relaxkonos/mysql/{id:N}/data";
        var runtime = $"relaxkonos-mysql-{id:N}";
        var socket = $"/run/{runtime}";
        string[] initialize = ["--no-defaults", "--initialize-insecure", $"--datadir={data}", "--skip-networking", "--mysqlx=OFF"];
        var unit = $"""
            [Unit]
            Description=RelaxKonOS managed MySQL instance {id:N}
            [Service]
            Type=simple
            User=relaxkonos-mysql
            Group=relaxkonos-mysql
            RuntimeDirectory={runtime}
            RuntimeDirectoryMode=0700
            UMask=0077
            ExecStart={Server} --no-defaults --datadir={data} --socket={socket}/mysql.sock --pid-file={socket}/mysqld.pid --skip-networking --mysqlx=OFF --skip-log-bin --secure-file-priv=NULL
            """;
        return new(id, data, socket, $"{runtime}.service", Array.AsReadOnly(initialize), unit + "\n");
    }
}
